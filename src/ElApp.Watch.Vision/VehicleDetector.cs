using System.Collections.Concurrent;
using OpenCvSharp;
using OpenCvSharp.Dnn;

namespace ElApp.Watch.Vision;

/// <summary>
/// Finds vehicles in a camera frame using a YOLOX-s object detector (COCO classes, int8,
/// Apache-2.0, from github.com/opencv/opencv_zoo - see Assets/Models/NOTICE.md). Unlike a
/// motion-blob heuristic, this classifies objects directly, so it needs no per-camera ROI or
/// resolution-specific tuning - the same model works on any frame size or camera angle.
///
/// OpenCV's dnn backend crashed the process (access violation inside Net.Forward) when called
/// from whichever thread pool thread happened to be free, even one call at a time under a lock -
/// it appears to need consistent thread affinity, not just non-overlapping calls. So every native
/// call here (model load included) runs on one dedicated, long-lived thread; every public method
/// hands work to that thread and blocks the caller until it's done.
/// </summary>
public sealed class VehicleDetector : IDisposable
{
    private const int InputSize = 640;
    private const float ConfidenceThreshold = 0.35f;
    private const float NmsThreshold = 0.5f;
    private const int ChannelsPerAnchor = 85; // 4 box coords + 1 objectness + 80 COCO class scores
    private const int ClassCount = 80;
    private static readonly int[] Strides = [8, 16, 32];

    // COCO class ids this app treats as "a vehicle": car, motorcycle, bus, truck.
    private static readonly HashSet<int> VehicleClassIds = [2, 3, 5, 7];

    private readonly BlockingCollection<Action> _workQueue = new();
    private readonly Thread _thread;
    private readonly ManualResetEventSlim _ready = new(false);
    private Net? _net;
    private (int GridX, int GridY, int Stride)[]? _anchors;
    private Exception? _initError;

    public VehicleDetector(string modelPath)
    {
        _thread = new Thread(() => RunOnDedicatedThread(modelPath))
        {
            IsBackground = true,
            Name = "VehicleDetection"
        };
        _thread.Start();
        _ready.Wait();

        if (_initError is not null)
        {
            throw new InvalidOperationException($"Failed to load vehicle detection model at '{modelPath}'.", _initError);
        }
    }

    /// <summary>
    /// Runs detection over the whole frame and returns the largest vehicle-classified box, or
    /// null if none was found. "Largest" is used as a simple proxy for "closest to this camera",
    /// which is normally the vehicle actually at this camera's pump. Blocks the calling thread
    /// until the dedicated detection thread has processed this frame.
    /// </summary>
    public VehicleDetection? DetectLargestVehicle(Mat frameBgr)
    {
        var tcs = new TaskCompletionSource<VehicleDetection?>(TaskCreationOptions.RunContinuationsAsynchronously);
        try
        {
            _workQueue.Add(() =>
            {
                try
                {
                    tcs.SetResult(DetectOnDedicatedThread(frameBgr));
                }
                catch (Exception ex)
                {
                    tcs.SetException(ex);
                }
            });
        }
        catch (InvalidOperationException)
        {
            // Dispose() already called CompleteAdding(): an analysis task that started just before
            // shutdown raced past CameraSourceService's throttle check and got here anyway. Treat it
            // exactly like "no vehicle found this frame" instead of crashing the app on close.
            return null;
        }
        return tcs.Task.GetAwaiter().GetResult();
    }

    private void RunOnDedicatedThread(string modelPath)
    {
        try
        {
            _net = CvDnn.ReadNetFromOnnx(modelPath) ?? throw new InvalidOperationException($"Failed to load vehicle detection model at '{modelPath}'.");
            _anchors = BuildAnchors();
        }
        catch (Exception ex)
        {
            _initError = ex;
        }
        finally
        {
            _ready.Set();
        }

        if (_initError is not null)
        {
            return;
        }

        foreach (Action work in _workQueue.GetConsumingEnumerable())
        {
            work();
        }

        _net!.Dispose();
    }

    private VehicleDetection? DetectOnDedicatedThread(Mat frameBgr)
    {
        double scale = Math.Min((double)InputSize / frameBgr.Rows, (double)InputSize / frameBgr.Cols);
        int scaledWidth = Math.Max(1, (int)Math.Round(frameBgr.Cols * scale));
        int scaledHeight = Math.Max(1, (int)Math.Round(frameBgr.Rows * scale));

        using var letterboxed = new Mat(new Size(InputSize, InputSize), MatType.CV_8UC3, new Scalar(114, 114, 114));
        using (var resized = new Mat())
        {
            Cv2.Resize(frameBgr, resized, new Size(scaledWidth, scaledHeight), interpolation: InterpolationFlags.Linear);
            using var target = new Mat(letterboxed, new Rect(0, 0, scaledWidth, scaledHeight));
            resized.CopyTo(target);
        }

        using var blob = CvDnn.BlobFromImage(letterboxed, scaleFactor: 1.0, size: new Size(InputSize, InputSize),
            mean: default, swapRB: true, crop: false);

        _net!.SetInput(blob);
        float[] raw;
        using (Mat output = _net.Forward())
        using (Mat flatOutput = output.Reshape(1, (int)output.Total()))
        {
            flatOutput.GetArray(out raw);
        }

        var boxes = new List<Rect>();
        var scores = new List<float>();

        for (int i = 0; i < _anchors!.Length; i++)
        {
            int offset = i * ChannelsPerAnchor;
            float objectness = raw[offset + 4];
            if (objectness < ConfidenceThreshold)
            {
                continue; // cheap reject before scanning all 80 class scores
            }

            float bestClassScore = 0f;
            int bestClassId = -1;
            for (int c = 0; c < ClassCount; c++)
            {
                float classScore = raw[offset + 5 + c];
                if (classScore > bestClassScore)
                {
                    bestClassScore = classScore;
                    bestClassId = c;
                }
            }
            if (!VehicleClassIds.Contains(bestClassId))
            {
                continue;
            }

            float confidence = objectness * bestClassScore;
            if (confidence < ConfidenceThreshold)
            {
                continue;
            }

            (int gridX, int gridY, int stride) = _anchors[i];
            float centerX = (raw[offset] + gridX) * stride;
            float centerY = (raw[offset + 1] + gridY) * stride;
            float width = MathF.Exp(raw[offset + 2]) * stride;
            float height = MathF.Exp(raw[offset + 3]) * stride;

            int x = (int)Math.Round((centerX - width / 2) / scale);
            int y = (int)Math.Round((centerY - height / 2) / scale);
            int boxWidth = (int)Math.Round(width / scale);
            int boxHeight = (int)Math.Round(height / scale);

            boxes.Add(new Rect(x, y, boxWidth, boxHeight));
            scores.Add(confidence);
        }

        if (boxes.Count == 0)
        {
            return null;
        }

        CvDnn.NMSBoxes(boxes, scores, ConfidenceThreshold, NmsThreshold, out int[] keep);
        if (keep.Length == 0)
        {
            return null;
        }

        int bestIndex = keep[0];
        long bestArea = (long)boxes[bestIndex].Width * boxes[bestIndex].Height;
        foreach (int index in keep)
        {
            long area = (long)boxes[index].Width * boxes[index].Height;
            if (area > bestArea)
            {
                bestArea = area;
                bestIndex = index;
            }
        }

        return new VehicleDetection(boxes[bestIndex], scores[bestIndex]);
    }

    private static (int, int, int)[] BuildAnchors()
    {
        var anchors = new List<(int, int, int)>();
        foreach (int stride in Strides)
        {
            int gridSize = InputSize / stride;
            for (int y = 0; y < gridSize; y++)
            {
                for (int x = 0; x < gridSize; x++)
                {
                    anchors.Add((x, y, stride));
                }
            }
        }
        return anchors.ToArray();
    }

    public void Dispose()
    {
        _workQueue.CompleteAdding();
        _thread.Join();
        _workQueue.Dispose();
        _ready.Dispose();
    }
}
