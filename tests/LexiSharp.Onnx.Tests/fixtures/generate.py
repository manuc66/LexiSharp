"""Generates the tiny ONNX graphs the tests run against.

These are not models in any useful sense: they gather a token embedding and pool it, which is
enough to exercise tokenization, input binding (int64/int32), output types (float32/float16),
pooling and score extraction without downloading or licensing a checkpoint. The weights are
seeded, so the fixtures are reproducible.

    python -m venv .venv && .venv/bin/pip install onnx numpy
    .venv/bin/python generate.py
"""

import numpy as np
import onnx
from onnx import TensorProto, helper, numpy_helper

VOCAB = 32
HIDDEN = 8
OPSET = 17
IR_VERSION = 8
SEED = 42

rng = np.random.default_rng(SEED)
embedding = rng.normal(0.0, 0.5, (VOCAB, HIDDEN)).astype(np.float32)
weights = rng.normal(0.0, 1.0, (HIDDEN, 1)).astype(np.float32)


def save(graph, path):
    model = helper.make_model(
        graph,
        opset_imports=[helper.make_opsetid("", OPSET)],
        producer_name="lexisharp-onnx-tests",
    )
    model.ir_version = IR_VERSION
    onnx.checker.check_model(model)
    onnx.save(model, path)
    print(f"wrote {path} ({len(model.SerializeToString())} bytes)")


def encoder_graph(ids_type, output_rank=3, output_type=TensorProto.FLOAT, matrix=embedding, mask_type=None, symbolic_hidden=False):
    inputs = [
        helper.make_tensor_value_info("input_ids", ids_type, ["batch", "seq"]),
        helper.make_tensor_value_info("attention_mask", mask_type or ids_type, ["batch", "seq"]),
    ]

    nodes = []

    if ids_type not in (TensorProto.INT64, TensorProto.INT32):
        nodes.append(helper.make_node("Cast", ["input_ids"], ["ids64"], to=TensorProto.INT64))
        input_ids = "ids64"
    else:
        input_ids = "input_ids"

    nodes += [
        helper.make_node("Gather", ["embedding", input_ids], ["gathered"], axis=0),
        helper.make_node("Cast", ["attention_mask"], ["mask_float"], to=TensorProto.FLOAT),
        helper.make_node("Unsqueeze", ["mask_float", "axes"], ["mask"]),
        helper.make_node("Mul", ["gathered", "mask"], ["masked"]),
    ]
    initializers = [
        numpy_helper.from_array(matrix, "embedding"),
        numpy_helper.from_array(np.array([2], dtype=np.int64), "axes"),
    ]

    if output_rank == 2:
        nodes.append(helper.make_node("ReduceSum", ["masked", "sum_axes"], ["pooled"], keepdims=0))
        initializers.append(numpy_helper.from_array(np.array([1], dtype=np.int64), "sum_axes"))
        computed = "pooled"
        shape = ["batch", HIDDEN]
    else:
        computed = "masked"
        shape = ["batch", "seq", "hidden"] if symbolic_hidden else ["batch", "seq", HIDDEN]

    if output_rank == 4:
        nodes.append(helper.make_node("Unsqueeze", ["masked", "rank_axes"], ["ranked"]))
        initializers.append(numpy_helper.from_array(np.array([3], dtype=np.int64), "rank_axes"))
        computed = "ranked"
        shape = ["batch", "seq", HIDDEN, 1]

    if output_type != TensorProto.FLOAT:
        nodes.append(helper.make_node("Cast", [computed], ["typed_output"], to=output_type))
        computed = "typed_output"

    nodes.append(helper.make_node("Identity", [computed], ["last_hidden_state"]))
    output = helper.make_tensor_value_info("last_hidden_state", output_type, shape)
    return helper.make_graph(nodes, "tiny-encoder", inputs, [output], initializers)


def cross_encoder_graph(ids_type, output_type=TensorProto.FLOAT, labels=1, rank=2):
    inputs = [
        helper.make_tensor_value_info("input_ids", ids_type, ["batch", "seq"]),
        helper.make_tensor_value_info("attention_mask", ids_type, ["batch", "seq"]),
        helper.make_tensor_value_info("token_type_ids", ids_type, ["batch", "seq"]),
    ]

    nodes = []

    if ids_type not in (TensorProto.INT64, TensorProto.INT32):
        nodes.append(helper.make_node("Cast", ["input_ids"], ["ids64"], to=TensorProto.INT64))
        input_ids = "ids64"
    else:
        input_ids = "input_ids"

    nodes += [
        helper.make_node("Gather", ["embedding", input_ids], ["gathered"], axis=0),
        helper.make_node("Cast", ["attention_mask"], ["mask_float"], to=TensorProto.FLOAT),
        helper.make_node("Unsqueeze", ["mask_float", "axes"], ["mask"]),
        helper.make_node("Mul", ["gathered", "mask"], ["masked"]),
        helper.make_node("ReduceSum", ["masked", "sum_axes"], ["pooled"], keepdims=0),
        helper.make_node("MatMul", ["pooled", "weights"], ["logits_float"]),
    ]
    initializers = [
        numpy_helper.from_array(embedding, "embedding"),
        numpy_helper.from_array(
            weights if labels == 1 else np.concatenate([weights, rng.normal(0.0, 1.0, (HIDDEN, labels - 1)).astype(np.float32)], axis=1),
            "weights"),
        numpy_helper.from_array(np.array([2], dtype=np.int64), "axes"),
        numpy_helper.from_array(np.array([1], dtype=np.int64), "sum_axes"),
    ]

    computed = "logits_float"

    if output_type != TensorProto.FLOAT:
        nodes.append(helper.make_node("Cast", [computed], ["typed_logits"], to=output_type))
        computed = "typed_logits"

    if rank == 1:
        nodes.append(helper.make_node("Squeeze", [computed, "rank_axes"], ["ranked"]))
        initializers.append(numpy_helper.from_array(np.array([1], dtype=np.int64), "rank_axes"))
        computed = "ranked"
    elif rank == 3:
        nodes.append(helper.make_node("Unsqueeze", [computed, "rank_axes"], ["ranked"]))
        initializers.append(numpy_helper.from_array(np.array([2], dtype=np.int64), "rank_axes"))
        computed = "ranked"

    nodes.append(helper.make_node("Identity", [computed], ["logits"]))
    shape = ["batch"] if rank == 1 else (["batch", labels, 1] if rank == 3 else ["batch", labels])
    output = helper.make_tensor_value_info("logits", output_type, shape)
    return helper.make_graph(nodes, "tiny-cross-encoder", inputs, [output], initializers)


KNOWN = np.array([[i * 10 + d for d in range(HIDDEN)] for i in range(VOCAB)], dtype=np.float32)

if __name__ == "__main__":
    save(encoder_graph(TensorProto.INT64), "tiny-encoder.onnx")
    save(encoder_graph(TensorProto.INT64, matrix=KNOWN), "tiny-encoder-known.onnx")
    save(encoder_graph(TensorProto.INT32), "tiny-encoder-int32.onnx")
    save(encoder_graph(TensorProto.INT64, output_type=TensorProto.FLOAT16), "tiny-encoder-fp16.onnx")
    save(encoder_graph(TensorProto.INT64, output_rank=2), "tiny-encoder-pooled.onnx")
    save(encoder_graph(TensorProto.INT64, output_type=TensorProto.INT32), "tiny-encoder-int32-output.onnx")
    save(encoder_graph(TensorProto.INT8), "tiny-encoder-int8-input.onnx")
    save(encoder_graph(TensorProto.INT64, mask_type=TensorProto.INT32), "tiny-encoder-mixed-mask.onnx")
    save(encoder_graph(TensorProto.INT64, output_rank=4), "tiny-encoder-rank4.onnx")
    save(encoder_graph(TensorProto.INT64, symbolic_hidden=True), "tiny-encoder-dynamic.onnx")
    save(cross_encoder_graph(TensorProto.INT64), "tiny-cross-encoder.onnx")
    save(cross_encoder_graph(TensorProto.INT32), "tiny-cross-encoder-int32.onnx")
    save(cross_encoder_graph(TensorProto.INT64, output_type=TensorProto.FLOAT16), "tiny-cross-encoder-fp16.onnx")
    save(cross_encoder_graph(TensorProto.INT64, labels=3), "tiny-cross-encoder-3logits.onnx")
    save(cross_encoder_graph(TensorProto.INT8), "tiny-cross-encoder-int8-input.onnx")
    save(cross_encoder_graph(TensorProto.INT64, output_type=TensorProto.INT32), "tiny-cross-encoder-int32-output.onnx")
    save(cross_encoder_graph(TensorProto.INT64, rank=1), "tiny-cross-encoder-rank1.onnx")
    save(cross_encoder_graph(TensorProto.INT64, rank=3), "tiny-cross-encoder-rank3.onnx")
