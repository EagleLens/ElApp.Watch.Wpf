## object_detection_yolox_2022nov_int8.onnx

A YOLOX-s object detector (COCO classes, int8-quantized), used to find vehicles in a
camera frame for `WpfApp1.PumpMonitoring.VehicleDetector`.

- Source: https://github.com/opencv/opencv_zoo/tree/main/models/object_detection_yolox
- License: Apache License 2.0 (see `LICENSE-opencv_zoo.txt` in this folder)
- Original detector: [YOLOX](https://github.com/Megvii-BaseDetection/YOLOX) (Megvii, Apache 2.0)

## yolo_v9_t_384_license_plate_end2end.onnx

A YOLOv9-tiny object detector (single class: "License Plate", NMS baked into the ONNX graph),
used to find the plate region within a captured vehicle photo, for
`WpfApp1.PumpMonitoring.PlateReader`. Run via Microsoft.ML.OnnxRuntime rather than OpenCV's dnn
module, since OpenCV's ONNX importer doesn't support the graph's NonMaxSuppression op.

- Source: https://github.com/ankandrew/open-image-models (release asset `yolo-v9-t-384-license-plates-end2end.onnx`)
- License: MIT (see `LICENSE-open-image-models.txt` in this folder)

## fast_plate_ocr_global_mobile_vit_v2.onnx

A fixed-slot (9 character positions) plate-text classifier trained on plates from 65+ countries,
used to read the characters out of the cropped plate region, for
`WpfApp1.PumpMonitoring.PlateReader`. Also run via Microsoft.ML.OnnxRuntime.

- Source: https://github.com/ankandrew/fast-plate-ocr (release asset `global_mobile_vit_v2_ocr.onnx`)
- License: MIT (see `LICENSE-fast-plate-ocr.txt` in this folder)
