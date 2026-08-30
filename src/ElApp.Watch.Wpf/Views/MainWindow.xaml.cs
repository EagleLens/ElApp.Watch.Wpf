using System.Diagnostics;
using System.IO;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Shapes;
using System.Windows.Threading;
using ElApp.Watch.Domain;
using ElApp.Watch.Vision;
using Microsoft.ML.OnnxRuntime;
using OpenCvSharp;
using OpenCvSharp.WpfExtensions;
using Window = System.Windows.Window;
using Path = System.IO.Path;

namespace ElApp.Watch.Wpf.Views
{
	/// <summary>
	/// Interaction logic for MainWindow.xaml
	/// </summary>
	public partial class MainWindow : Window
	{
		private sealed class TileHandle
		{
			public required ImageBrush VideoBrush { get; init; }
			public required Ellipse TileDot { get; init; }
			public required Ellipse SidebarDot { get; init; }
			public required TextBlock BadgeText { get; init; }
			public required Image SnapshotImage { get; init; }
			public required UIElement SnapshotOverlay { get; init; }
			public TextBlock? VehicleStatusIcon { get; init; }
			public TextBlock? VehicleStatusText { get; init; }
			public bool Online { get; set; }
			public PumpMonitor? Monitor { get; set; }
		}

		/// <summary>Tracks whether a tile's next vehicle-detection pass is due, and whether one is already running in the background.</summary>
		private sealed class AnalysisThrottle
		{
			public readonly Stopwatch SinceLastAnalysis = Stopwatch.StartNew();
			public bool Busy;
		}

		private static readonly SolidColorBrush LiveBadgeRedBrush = new(Color.FromRgb(0xF8, 0x71, 0x71));

		// Running the vehicle detector on every captured frame made the capture loop (and so
		// on-screen playback) as slow as inference itself - ~250ms/call, and shared by every
		// pump through one lock (OpenCV's dnn backend isn't safe under concurrent native calls).
		// Detection now runs on a background task at this fixed cadence instead, decoupled from
		// frame capture/display, which stay at the source's native rate. AnalysisFps (not the
		// video's own fps) is what the state machine's stillness/grace timers are calibrated to,
		// since that's the actual rate presence gets re-checked at.
		private const int AnalysisIntervalMs = 1000;
		private const double AnalysisFps = 1000.0 / AnalysisIntervalMs;

		// Shared across every pump: separate Net instances (even serialized with a lock) crashed
		// the process, so there is exactly one detector, lazily built on whichever pump's
		// background thread needs it first.
		private readonly Lazy<VehicleDetector> _vehicleDetector = new(
			() => new VehicleDetector(Path.Combine(AppContext.BaseDirectory, "Assets", "Models", "object_detection_yolox_2022nov_int8.onnx")),
			LazyThreadSafetyMode.ExecutionAndPublication);

		// Only runs once per captured photo (not per frame), so - unlike _vehicleDetector - this
		// doesn't need dedicated-thread handling; built lazily on whichever pump's PhotoCaptured
		// handler needs it first.
		private readonly Lazy<PlateReader> _plateReader = new(
			() => new PlateReader(
				Path.Combine(AppContext.BaseDirectory, "Assets", "Models", "yolo_v9_t_384_license_plate_end2end.onnx"),
				Path.Combine(AppContext.BaseDirectory, "Assets", "Models", "fast_plate_ocr_global_mobile_vit_v2.onnx")),
			LazyThreadSafetyMode.ExecutionAndPublication);

		// Global: hides every tile's status text while leaving its icon (and icon color) visible.
		private bool _showStatusLabels = true;

		private readonly DispatcherTimer _clockTimer;
		private readonly CancellationTokenSource _cameraCts = new();
		private readonly int _pumpCount = Math.Max(4, System.Random.Shared.Next(1, 9));
		private VideoCapture? _pump1Capture;
		private VideoCapture? _pump2Capture;
		private VideoCapture? _pump3Capture;
		private VideoCapture? _pump4Capture;
		private TileHandle _pump1 = null!;
		private TileHandle? _pump2;
		private TileHandle? _pump3;
		private TileHandle? _pump4;

		//noblink
		public MainWindow()
		{
			InitializeComponent();

			BuildCameraTiles(_pumpCount);

			_clockTimer = new DispatcherTimer
			{
				Interval = TimeSpan.FromSeconds(1)
			};
			_clockTimer.Tick += (_, _) => ClockText.Text = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss");
			_clockTimer.Start();
			ClockText.Text = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss");

			Closing += (_, _) => StopCameras();

			_ = StartPump1CameraAsync(_pump1, _cameraCts.Token);

			if (_pump2 is not null)
			{
				string videoPath = Path.Combine(AppContext.BaseDirectory, "Assets", "cctv_multi_vehicle_test.mp4");
				_ = StartPumpVideoAsync(_pump2, videoPath, pumpId: "Pump2", pumpNumber: 2, _cameraCts.Token);
			}

			if (_pump3 is not null)
			{
				string videoPath = Path.Combine(AppContext.BaseDirectory, "Assets", "cctv_multi_vehicle_test1.mp4");
				_ = StartPumpVideoAsync(_pump3, videoPath, pumpId: "Pump3", pumpNumber: 3, _cameraCts.Token);
			}

			if (_pump4 is not null)
			{
				string videoPath = Path.Combine(AppContext.BaseDirectory, "Assets", "cctv_multi_vehicle_test2.mp4");
				_ = StartPumpVideoAsync(_pump4, videoPath, pumpId: "Pump4", pumpNumber: 4, _cameraCts.Token);
			}
		}

		private void BuildCameraTiles(int count)
		{
			CameraGrid.Children.Clear();
			CameraListPanel.Children.Clear();

			int columns = (int)Math.Ceiling(Math.Sqrt(count));
			int rows = (int)Math.Ceiling(count / (double)columns);
			CameraGrid.Rows = rows;
			CameraGrid.Columns = columns;

			var tileBrush = (Brush)FindResource("TileBrush");
			var borderBrush = (Brush)FindResource("BorderBrush1");
			var onlineBrush = (Brush)FindResource("OnlineBrush");
			var textPrimaryBrush = (Brush)FindResource("TextPrimaryBrush");
			var textSecondaryBrush = (Brush)FindResource("TextSecondaryBrush");
			var cameraListItemStyle = (Style)FindResource("CameraListItem");

			for (int i = 0; i < count; i++)
			{
				bool isDynamicTile = i <= 3;
				int pumpNumber = i + 1;
				string assetPath = $"pack://application:,,,/Assets/pump{(i % 4) + 1}.jpg";

				var imageBrush = new ImageBrush
				{
					ImageSource = new BitmapImage(new Uri(assetPath, UriKind.Absolute)),
					Stretch = Stretch.UniformToFill
				};

				var rect = new Rectangle { RadiusX = 10, RadiusY = 10, Fill = imageBrush };

				var statusDot = new Ellipse
				{
					Width = 7,
					Height = 7,
					Fill = isDynamicTile ? textSecondaryBrush : onlineBrush,
					VerticalAlignment = VerticalAlignment.Center,
					Margin = new Thickness(0, 0, 6, 0)
				};

				var nameStack = new StackPanel { Orientation = Orientation.Horizontal };
				nameStack.Children.Add(statusDot);
				nameStack.Children.Add(new TextBlock { Text = $"Pump {pumpNumber}", Foreground = Brushes.White, FontSize = 12, FontWeight = FontWeights.Medium });

				var nameBadge = new Border
				{
					VerticalAlignment = VerticalAlignment.Top,
					HorizontalAlignment = HorizontalAlignment.Left,
					Margin = new Thickness(10),
					Padding = new Thickness(8, 3, 8, 3),
					CornerRadius = new CornerRadius(4),
					Background = Brushes.Black,
					Opacity = 0.55,
					Child = nameStack
				};

				var badgeText = new TextBlock
				{
					Text = isDynamicTile ? "DETECTING" : "LIVE",
					Foreground = isDynamicTile ? textSecondaryBrush : LiveBadgeRedBrush,
					FontSize = 10,
					FontWeight = FontWeights.Bold
				};

				var liveBadge = new Border
				{
					VerticalAlignment = VerticalAlignment.Top,
					HorizontalAlignment = HorizontalAlignment.Right,
					Margin = new Thickness(10),
					Padding = new Thickness(6, 2, 6, 2),
					CornerRadius = new CornerRadius(4),
					Background = Brushes.Black,
					Opacity = 0.55,
					Child = badgeText
				};

				var snapshotImage = new Image { Stretch = Stretch.UniformToFill };
				var snapshotCaption = new Border
				{
					VerticalAlignment = VerticalAlignment.Top,
					Background = Brushes.Black,
					Opacity = 0.65,
					Child = new TextBlock { Text = "SNAPSHOT", Foreground = Brushes.White, FontSize = 8, FontWeight = FontWeights.Bold, HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(0, 1, 0, 1) }
				};
				var snapshotContent = new Grid();
				snapshotContent.Children.Add(snapshotImage);
				snapshotContent.Children.Add(snapshotCaption);

				var snapshotOverlay = new Border
				{
					Width = 104,
					Height = 78,
					VerticalAlignment = VerticalAlignment.Bottom,
					HorizontalAlignment = HorizontalAlignment.Right,
					Margin = new Thickness(10),
					CornerRadius = new CornerRadius(6),
					BorderBrush = (Brush)FindResource("AccentBrush"),
					BorderThickness = new Thickness(2),
					Background = Brushes.Black,
					ClipToBounds = true,
					Visibility = Visibility.Collapsed,
					Child = snapshotContent
				};

				TextBlock? vehicleStatusIcon = null;
				TextBlock? vehicleStatusText = null;
				Border? vehicleStatusBadge = null;
				if (isDynamicTile)
				{
					vehicleStatusIcon = new TextBlock
					{
						Text = VehicleStatusDisplay[PumpState.Empty].Icon,
						Foreground = textSecondaryBrush,
						FontSize = 12,
						FontWeight = FontWeights.Bold,
						VerticalAlignment = VerticalAlignment.Center,
						Margin = new Thickness(0, 0, 6, 0)
					};

					vehicleStatusText = new TextBlock
					{
						Text = "Pump empty",
						Foreground = textSecondaryBrush,
						FontSize = 10,
						FontWeight = FontWeights.Bold,
						Visibility = _showStatusLabels ? Visibility.Visible : Visibility.Collapsed
					};

					var vehicleStatusStack = new StackPanel { Orientation = Orientation.Horizontal };
					vehicleStatusStack.Children.Add(vehicleStatusIcon);
					vehicleStatusStack.Children.Add(vehicleStatusText);

					vehicleStatusBadge = new Border
					{
						VerticalAlignment = VerticalAlignment.Bottom,
						HorizontalAlignment = HorizontalAlignment.Left,
						Margin = new Thickness(10),
						Padding = new Thickness(8, 3, 8, 3),
						CornerRadius = new CornerRadius(4),
						Background = Brushes.Black,
						Opacity = 0.7,
						Child = vehicleStatusStack
					};
				}

				var tileGrid = new Grid();
				tileGrid.Children.Add(rect);
				tileGrid.Children.Add(nameBadge);
				tileGrid.Children.Add(liveBadge);
				tileGrid.Children.Add(snapshotOverlay);
				if (vehicleStatusBadge is not null)
				{
					tileGrid.Children.Add(vehicleStatusBadge);
				}

				var tileBorder = new Border
				{
					Margin = new Thickness(8),
					CornerRadius = new CornerRadius(10),
					Background = tileBrush,
					BorderBrush = borderBrush,
					BorderThickness = new Thickness(1),
					ClipToBounds = true,
					Child = tileGrid
				};

				CameraGrid.Children.Add(tileBorder);

				var sidebarDot = new Ellipse { Width = 8, Height = 8, Fill = isDynamicTile ? textSecondaryBrush : onlineBrush, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 10, 0) };
				var sideStack = new StackPanel { Orientation = Orientation.Horizontal };
				sideStack.Children.Add(sidebarDot);
				sideStack.Children.Add(new TextBlock { Text = $"Pump {pumpNumber}", Foreground = textPrimaryBrush, FontSize = 13 });
				CameraListPanel.Children.Add(new Border { Style = cameraListItemStyle, Child = sideStack });

				if (isDynamicTile)
				{
					var handle = new TileHandle
					{
						VideoBrush = imageBrush,
						TileDot = statusDot,
						SidebarDot = sidebarDot,
						BadgeText = badgeText,
						SnapshotImage = snapshotImage,
						SnapshotOverlay = snapshotOverlay,
						VehicleStatusIcon = vehicleStatusIcon,
						VehicleStatusText = vehicleStatusText
					};

					switch (i)
					{
						case 0: _pump1 = handle; break;
						case 1: _pump2 = handle; break;
						case 2: _pump3 = handle; break;
						default: _pump4 = handle; break;
					}
				}
			}

			UpdateOnlineCountText();
		}

		private async Task StartPump1CameraAsync(TileHandle tile, CancellationToken token)
		{
			VideoCapture? capture = await Task.Run(() =>
			{
				for (int index = 0; index < 4; index++)
				{
					var candidate = new VideoCapture(index, VideoCaptureAPIs.DSHOW);
					if (candidate.IsOpened())
					{
						return candidate;
					}
					candidate.Dispose();
				}
				return null;
			}, token);

			if (token.IsCancellationRequested)
			{
				capture?.Dispose();
				return;
			}

			if (capture is null)
			{
				SetTileStatus(tile, online: false, "NO CAMERA");
				return;
			}

			_pump1Capture = capture;
			SetTileStatus(tile, online: true, "LIVE");
			// Loading the vehicle-detection model is not free - keep it off the UI thread.
			await Task.Run(() => AttachPumpMonitor(tile, pumpId: "Pump1", pumpNumber: 1, AnalysisFps), token);

			await Task.Run(() =>
			{
				using var frame = new Mat();
				const int maxConsecutiveFailures = 30;
				int consecutiveFailures = 0;
				var throttle = new AnalysisThrottle();

				while (!token.IsCancellationRequested)
				{
					if (!capture.Read(frame) || frame.Empty())
					{
						consecutiveFailures++;
						if (consecutiveFailures >= maxConsecutiveFailures)
						{
							break;
						}
						continue;
					}

					consecutiveFailures = 0;
					TryAnalyzeThrottled(tile, frame, throttle);
					PublishFrame(tile.VideoBrush, frame);
				}
			}, token);

			if (!token.IsCancellationRequested)
			{
				SetTileStatus(tile, online: false, "OFFLINE");
			}
		}

		private async Task StartPumpVideoAsync(TileHandle tile, string filePath, string pumpId, int pumpNumber, CancellationToken token, OpenCvSharp.Rect? roi = null)
		{
			VideoCapture? capture = await Task.Run(() =>
			{
				if (!File.Exists(filePath))
				{
					return null;
				}
				var candidate = new VideoCapture(filePath);
				return candidate.IsOpened() ? candidate : null;
			}, token);

			if (token.IsCancellationRequested)
			{
				capture?.Dispose();
				return;
			}

			if (capture is null)
			{
				SetTileStatus(tile, online: false, "NO VIDEO");
				return;
			}

			switch (pumpNumber)
			{
				case 2: _pump2Capture = capture; break;
				case 3: _pump3Capture = capture; break;
				case 4: _pump4Capture = capture; break;
			}
			SetTileStatus(tile, online: true, "LIVE");

			double fps = capture.Fps > 0 ? capture.Fps : 25;
			int frameDelayMs = Math.Max(1, (int)Math.Round(1000.0 / fps));
			// Loading the vehicle-detection model is not free - keep it off the UI thread.
			await Task.Run(() => AttachPumpMonitor(tile, pumpId, pumpNumber, AnalysisFps, roi), token);

			await Task.Run(() =>
			{
				using var frame = new Mat();
				var throttle = new AnalysisThrottle();
				while (!token.IsCancellationRequested)
				{
					if (!capture.Read(frame) || frame.Empty())
					{
						capture.Set(VideoCaptureProperties.PosFrames, 0);
						continue;
					}

					TryAnalyzeThrottled(tile, frame, throttle);
					PublishFrame(tile.VideoBrush, frame);
					Thread.Sleep(frameDelayMs);
				}
			}, token);
		}

		/// <summary>
		/// Kicks off vehicle detection for this frame on a background task if enough time has
		/// passed since the last pass and none is already in flight - otherwise leaves the frame
		/// unanalyzed. This is what keeps capture/playback running at full speed regardless of how
		/// slow (or contended) detection is.
		/// </summary>
		private static void TryAnalyzeThrottled(TileHandle tile, Mat frame, AnalysisThrottle throttle)
		{
			if (throttle.Busy || throttle.SinceLastAnalysis.ElapsedMilliseconds < AnalysisIntervalMs)
			{
				return;
			}

			throttle.Busy = true;
			throttle.SinceLastAnalysis.Restart();
			Mat frameClone = frame.Clone();
			_ = Task.Run(() =>
			{
				try
				{
					tile.Monitor!.ProcessFrame(frameClone);
				}
				finally
				{
					frameClone.Dispose();
					throttle.Busy = false;
				}
			});
		}

		private void AttachPumpMonitor(TileHandle tile, string pumpId, int pumpNumber, double fps, OpenCvSharp.Rect? roi = null)
		{
			var monitor = new PumpMonitor(pumpId, fps, _vehicleDetector.Value, roi);
			monitor.StatusChanged += (_, e) => Dispatcher.BeginInvoke(() => ApplyVehicleStatus(tile, e.State));
			monitor.PhotoCaptured += (_, e) => OnPumpPhotoCaptured(tile, pumpNumber, e.Frame);
			tile.Monitor = monitor;
		}

		private static readonly Dictionary<PumpState, (string Text, string BrushKey, string Icon)> VehicleStatusDisplay = new()
		{
			[PumpState.Empty] = ("Pump empty", "TextSecondaryBrush", "○"),         // ○
			[PumpState.VehicleComing] = ("Vehicle coming", "AccentBrush", "→"),    // →
			[PumpState.VehicleStopping] = ("Vehicle stopping", "AmberBrush", "⧖"), // ⧖ hourglass
			[PumpState.TakingPhoto] = ("Taking photo", "OfflineBrush", "◉"),       // ◉ shutter
			[PumpState.VehicleStopped] = ("Vehicle stopped", "OnlineBrush", "✓"),  // ✓
		};

		private void ApplyVehicleStatus(TileHandle tile, PumpState state)
		{
			(string text, string brushKey, string icon) = VehicleStatusDisplay[state];
			SetStatusVisual(tile, text, brushKey, icon);

			if (state == PumpState.Empty)
			{
				tile.SnapshotOverlay.Visibility = Visibility.Collapsed;
			}
		}

		/// <summary>Applies an icon/text/color combination to a tile's status badge, honoring the current label-visibility toggle.</summary>
		private void SetStatusVisual(TileHandle tile, string text, string brushKey, string icon)
		{
			var brush = (Brush)FindResource(brushKey);

			if (tile.VehicleStatusIcon is not null)
			{
				tile.VehicleStatusIcon.Text = icon;
				tile.VehicleStatusIcon.Foreground = brush;
			}
			if (tile.VehicleStatusText is not null)
			{
				tile.VehicleStatusText.Text = text;
				tile.VehicleStatusText.Foreground = brush;
				tile.VehicleStatusText.Visibility = _showStatusLabels ? Visibility.Visible : Visibility.Collapsed;
			}
		}

		private void OnPumpPhotoCaptured(TileHandle tile, int pumpNumber, Mat frame)
		{
			using (frame)
			{
				string? plateText = null;
				try
				{
					plateText = _plateReader.Value.ReadPlate(frame);
				}
				catch (OnnxRuntimeException)
				{
					// best-effort - the photo itself still gets saved and shown even if plate reading fails
				}

				// bin/Debug/<tfm> -> project root, so captures land in the project folder instead of build output
				string snapshotDir = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "Snapshots"));
				bool saved = false;
				try
				{
					Directory.CreateDirectory(snapshotDir);
					string plateSuffix = plateText is not null ? $"_{plateText}" : string.Empty;
					string filePath = Path.Combine(snapshotDir, $"pump{pumpNumber}{plateSuffix}_{DateTime.Now:yyyyMMdd_HHmmss}.jpg");
					Cv2.ImWrite(filePath, frame);
					saved = true;
				}
				catch (IOException)
				{
					// best-effort disk persistence; the in-memory snapshot below still shows regardless
				}

				BitmapSource bitmap = frame.ToBitmapSource();
				bitmap.Freeze();
				string statusText = (saved, plateText) switch
				{
					(true, not null) => $"Photo saved - {plateText}",
					(true, null) => "Photo saved (plate unclear)",
					(false, _) => "Photo captured (save failed)",
				};
				string statusIcon = saved ? "📷" : "⚠";
				Dispatcher.BeginInvoke(() =>
				{
					tile.SnapshotImage.Source = bitmap;
					tile.SnapshotOverlay.Visibility = Visibility.Visible;
					ShowTransientStatus(tile, statusText, saved ? "OnlineBrush" : "OfflineBrush", statusIcon);
				});
			}
		}

		/// <summary>
		/// Overrides a tile's status icon/text for a few seconds (e.g. to confirm a capture just
		/// happened), then restores whatever the pump's actual current state should show - so this
		/// never leaves a tile stuck displaying a stale message once its state has moved on.
		/// </summary>
		private void ShowTransientStatus(TileHandle tile, string text, string brushKey, string icon)
		{
			if (tile.VehicleStatusText is null)
			{
				return;
			}

			SetStatusVisual(tile, text, brushKey, icon);

			var revertTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(3.5) };
			revertTimer.Tick += (_, _) =>
			{
				revertTimer.Stop();
				if (tile.Monitor is not null)
				{
					ApplyVehicleStatus(tile, tile.Monitor.State);
				}
			};
			revertTimer.Start();
		}

		private void PublishFrame(ImageBrush brush, Mat frame)
		{
			BitmapSource bitmap = frame.ToBitmapSource();
			bitmap.Freeze();
			Dispatcher.BeginInvoke(() => brush.ImageSource = bitmap);
		}

		private void SetTileStatus(TileHandle tile, bool online, string badgeText)
		{
			Dispatcher.BeginInvoke(() =>
			{
				var brush = (Brush)FindResource(online ? "OnlineBrush" : "OfflineBrush");
				tile.TileDot.Fill = brush;
				tile.SidebarDot.Fill = brush;
				tile.BadgeText.Text = badgeText;
				tile.BadgeText.Foreground = brush;
				tile.Online = online;
				UpdateOnlineCountText();
			});
		}

		private void UpdateOnlineCountText()
		{
			int dynamicTiles = 1 + (_pump2 is not null ? 1 : 0) + (_pump3 is not null ? 1 : 0) + (_pump4 is not null ? 1 : 0);
			int alwaysOnline = _pumpCount - dynamicTiles;
			int online = alwaysOnline + (_pump1.Online ? 1 : 0) + (_pump2?.Online == true ? 1 : 0) + (_pump3?.Online == true ? 1 : 0) + (_pump4?.Online == true ? 1 : 0);
			OnlineCountText.Text = $"{online} / {_pumpCount} Cameras Online";
			OnlineCountDot.Fill = (Brush)FindResource(online == _pumpCount ? "OnlineBrush" : "OfflineBrush");
		}

		/// <summary>
		/// Global toggle: hides every tile's status text while its icon (and icon color) stays
		/// visible, since the icon alone still conveys the state at a glance.
		/// </summary>
		private void ToggleLabelsButton_Click(object sender, RoutedEventArgs e)
		{
			_showStatusLabels = !_showStatusLabels;
			ToggleLabelsButtonText.TextDecorations = _showStatusLabels ? null : System.Windows.TextDecorations.Strikethrough;
			ToggleLabelsButton.Opacity = _showStatusLabels ? 1.0 : 0.55;

			var visibility = _showStatusLabels ? Visibility.Visible : Visibility.Collapsed;
			foreach (TileHandle? tile in new[] { _pump1, _pump2, _pump3, _pump4 })
			{
				if (tile?.VehicleStatusText is not null)
				{
					tile.VehicleStatusText.Visibility = visibility;
				}
			}
		}

		private void StopCameras()
		{
			_cameraCts.Cancel();
			_pump1Capture?.Dispose();
			_pump1Capture = null;
			_pump2Capture?.Dispose();
			_pump2Capture = null;
			_pump3Capture?.Dispose();
			_pump3Capture = null;
			_pump4Capture?.Dispose();
			_pump4Capture = null;
			if (_vehicleDetector.IsValueCreated)
			{
				_vehicleDetector.Value.Dispose();
			}
			if (_plateReader.IsValueCreated)
			{
				_plateReader.Value.Dispose();
			}
		}
	}
}
