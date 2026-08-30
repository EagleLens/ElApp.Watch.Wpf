using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;
using OpenCvSharp;

namespace ElApp.Watch.Vision;

/// <summary>
/// Finds a license plate in a captured vehicle photo (YOLOv9-tiny, single class, MIT) and reads
/// its characters (fast-plate-ocr fixed-slot classifier, MIT) - see Assets/Models/NOTICE.md for
/// both. Runs through Microsoft.ML.OnnxRuntime rather than OpenCV's dnn module (used by
/// <see cref="VehicleDetector"/>): the plate detector has NMS baked into its ONNX graph as a
/// NonMaxSuppression op, which OpenCV's ONNX importer doesn't support. This only runs once per
/// captured photo (not per frame), so - unlike VehicleDetector - it doesn't need the dedicated
/// detection thread; OnnxRuntime's InferenceSession.Run is documented safe for concurrent calls.
/// </summary>
public sealed class PlateReader : IDisposable
{
    private const int DetectorInputSize = 384;
    private const float DetectorConfidenceThreshold = 0.3f;

    private const int OcrImgWidth = 140;
    private const int OcrImgHeight = 70;
    private const string OcrAlphabet = "0123456789ABCDEFGHIJKLMNOPQRSTUVWXYZ_";
    private const char OcrPadChar = '_';
    private const int OcrMaxSlots = 9;

    private readonly InferenceSession _detectorSession;
    private readonly InferenceSession _ocrSession;
    private readonly string _detectorInputName;
    private readonly string _detectorOutputName;
    private readonly string _ocrInputName;
    private readonly string _ocrOutputName;

    public PlateReader(string detectorModelPath, string ocrModelPath)
    {
        _detectorSession = new InferenceSession(detectorModelPath);
        _ocrSession = new InferenceSession(ocrModelPath);
        _detectorInputName = _detectorSession.InputMetadata.Keys.First();
        _detectorOutputName = _detectorSession.OutputMetadata.Keys.First();
        _ocrInputName = _ocrSession.InputMetadata.Keys.First();
        _ocrOutputName = _ocrSession.OutputMetadata.Keys.First();
    }

    /// <summary>Finds the highest-confidence plate in the photo and reads its text, or null if no plate was found.</summary>
    public string? ReadPlate(Mat vehiclePhotoBgr)
    {
        Rect? plateBox = DetectPlate(vehiclePhotoBgr);
        if (plateBox is null)
        {
            return null;
        }

        Rect clamped = plateBox.Value.Intersect(new Rect(0, 0, vehiclePhotoBgr.Cols, vehiclePhotoBgr.Rows));
        if (clamped.Width <= 0 || clamped.Height <= 0)
        {
            return null;
        }

        using var plateCrop = new Mat(vehiclePhotoBgr, clamped);
        string plate = ReadCharacters(plateCrop);
        return plate.Length > 0 ? plate : null;
    }

    private Rect? DetectPlate(Mat frameBgr)
    {
        // Centered letterbox to a square input, matching how the model was trained/exported.
        int h = frameBgr.Rows, w = frameBgr.Cols;
        double r = Math.Min((double)DetectorInputSize / h, (double)DetectorInputSize / w);
        int newUnpadW = (int)Math.Round(w * r), newUnpadH = (int)Math.Round(h * r);
        double dw = (DetectorInputSize - newUnpadW) / 2.0, dh = (DetectorInputSize - newUnpadH) / 2.0;
        int top = (int)Math.Round(dh - 0.1), bottom = (int)Math.Round(dh + 0.1);
        int left = (int)Math.Round(dw - 0.1), right = (int)Math.Round(dw + 0.1);

        using var resized = new Mat();
        Cv2.Resize(frameBgr, resized, new Size(newUnpadW, newUnpadH), interpolation: InterpolationFlags.Linear);
        using var letterboxed = new Mat();
        Cv2.CopyMakeBorder(resized, letterboxed, top, bottom, left, right, BorderTypes.Constant, new Scalar(114, 114, 114));

        // BGR -> RGB, HWC -> CHW, 0-255 -> 0-1, NCHW float32.
        var inputTensor = new DenseTensor<float>([1, 3, DetectorInputSize, DetectorInputSize]);
        for (int y = 0; y < DetectorInputSize; y++)
        {
            for (int x = 0; x < DetectorInputSize; x++)
            {
                Vec3b px = letterboxed.At<Vec3b>(y, x);
                inputTensor[0, 0, y, x] = px.Item2 / 255f; // R
                inputTensor[0, 1, y, x] = px.Item1 / 255f; // G
                inputTensor[0, 2, y, x] = px.Item0 / 255f; // B
            }
        }

        using var results = _detectorSession.Run([NamedOnnxValue.CreateFromTensor(_detectorInputName, inputTensor)]);
        float[] raw = results.First(v => v.Name == _detectorOutputName).AsTensor<float>().ToArray();

        // End2end export: NMS already applied, rows are [batchIdx, x1, y1, x2, y2, classId, score]
        // in the letterboxed 384x384 space.
        const int cols = 7;
        int rows = raw.Length / cols;

        Rect? best = null;
        float bestScore = 0f;
        for (int i = 0; i < rows; i++)
        {
            int off = i * cols;
            float score = raw[off + 6];
            if (score < DetectorConfidenceThreshold || score <= bestScore)
            {
                continue;
            }

            double ox1 = (raw[off + 1] - dw) / r;
            double oy1 = (raw[off + 2] - dh) / r;
            double ox2 = (raw[off + 3] - dw) / r;
            double oy2 = (raw[off + 4] - dh) / r;
            bestScore = score;
            best = new Rect((int)ox1, (int)oy1, (int)(ox2 - ox1), (int)(oy2 - oy1));
        }

        return best;
    }

    private string ReadCharacters(Mat plateCropBgr)
    {
        using var gray = new Mat();
        Cv2.CvtColor(plateCropBgr, gray, ColorConversionCodes.BGR2GRAY);
        using var resized = new Mat();
        Cv2.Resize(gray, resized, new Size(OcrImgWidth, OcrImgHeight), interpolation: InterpolationFlags.Linear);

        // NHWC uint8 - the model normalizes internally, so raw pixel values are fed as-is.
        var inputTensor = new DenseTensor<byte>([1, OcrImgHeight, OcrImgWidth, 1]);
        for (int y = 0; y < OcrImgHeight; y++)
        {
            for (int x = 0; x < OcrImgWidth; x++)
            {
                inputTensor[0, y, x, 0] = resized.At<byte>(y, x);
            }
        }

        using var results = _ocrSession.Run([NamedOnnxValue.CreateFromTensor(_ocrInputName, inputTensor)]);
        float[] raw = results.First(v => v.Name == _ocrOutputName).AsTensor<float>().ToArray();

        // Fixed-slot classifier: OcrMaxSlots independent heads of OcrAlphabet.Length classes each,
        // concatenated - no CTC decoding needed, just argmax per slot.
        var chars = new char[OcrMaxSlots];
        for (int slot = 0; slot < OcrMaxSlots; slot++)
        {
            int off = slot * OcrAlphabet.Length;
            int bestIdx = 0;
            float bestVal = float.NegativeInfinity;
            for (int c = 0; c < OcrAlphabet.Length; c++)
            {
                if (raw[off + c] > bestVal)
                {
                    bestVal = raw[off + c];
                    bestIdx = c;
                }
            }
            chars[slot] = OcrAlphabet[bestIdx];
        }

        return new string(chars).TrimEnd(OcrPadChar);
    }

    public void Dispose()
    {
        _detectorSession.Dispose();
        _ocrSession.Dispose();
    }
}
