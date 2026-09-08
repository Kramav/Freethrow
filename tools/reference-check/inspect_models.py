import sys
import onnxruntime as ort

for path in sys.argv[1:]:
    print("=" * 70)
    print(path)
    session = ort.InferenceSession(path, providers=["CPUExecutionProvider"])
    print("  inputs:")
    for i in session.get_inputs():
        print(f"    {i.name!r:24} {i.type:22} {i.shape}")
    print("  outputs:")
    for o in session.get_outputs():
        print(f"    {o.name!r:24} {o.type:22} {o.shape}")
