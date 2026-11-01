using LexiSharp.Core;

namespace LexiSharp.Onnx;

/// <summary>
/// A small FIFO cache of text → vector, keyed by the text and the query/passage role. It exists for
/// the query path, where the same few queries repeat; indexing a corpus produces unique texts and
/// is not helped by it.
/// </summary>
internal sealed class OnnxEmbeddingCache
{
    private readonly int _capacity;
    private readonly object _gate = new();
    private readonly Dictionary<Key, ReadOnlyMemory<float>> _values = [];
    private readonly Queue<Key> _order = [];

    public OnnxEmbeddingCache(int capacity) => _capacity = capacity;

    public bool TryGet(string text, EmbeddingUse use, out ReadOnlyMemory<float> vector)
    {
        if (_capacity == 0)
        {
            vector = default;
            return false;
        }

        lock (_gate)
            return _values.TryGetValue(new Key(text, use), out vector);
    }

    public void Set(string text, EmbeddingUse use, ReadOnlyMemory<float> vector)
    {
        if (_capacity == 0)
            return;

        Key key = new(text, use);

        lock (_gate)
        {
            if (_values.ContainsKey(key))
                return;

            if (_values.Count >= _capacity)
            {
                Key oldest = _order.Dequeue();
                _values.Remove(oldest);
            }

            _values[key] = vector;
            _order.Enqueue(key);
        }
    }

    public void Clear()
    {
        lock (_gate)
        {
            _values.Clear();
            _order.Clear();
        }
    }

    private readonly record struct Key(string Text, EmbeddingUse Use);
}
