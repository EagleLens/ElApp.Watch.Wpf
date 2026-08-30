using OpenCvSharp;

namespace ElApp.Watch.Vision;

/// <summary>A single detected object, in source-frame pixel coordinates.</summary>
public readonly record struct VehicleDetection(Rect Box, float Score);
