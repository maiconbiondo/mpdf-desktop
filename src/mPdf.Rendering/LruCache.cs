using System;
using System.Collections.Generic;

namespace mPdf.Rendering;

/// Cache LRU minimo (frente = mais recente). Usado pelo preview pra segurar so N paginas renderizadas
/// (memoria do prevhost). AOT-compativel (generics + Dictionary/LinkedList).
public sealed class LruCache<TKey, TValue> where TKey : notnull
{
    private readonly int _capacidade;
    private readonly Dictionary<TKey, LinkedListNode<(TKey Chave, TValue Valor)>> _map = new();
    private readonly LinkedList<(TKey Chave, TValue Valor)> _ordem = new();

    public LruCache(int capacidade) => _capacidade = Math.Max(1, capacidade);

    public int Count => _map.Count;
    public bool Contem(TKey chave) => _map.ContainsKey(chave);

    public bool TryGet(TKey chave, out TValue valor)
    {
        if (_map.TryGetValue(chave, out var node))
        {
            _ordem.Remove(node);
            _ordem.AddFirst(node);
            valor = node.Value.Valor;
            return true;
        }
        valor = default!;
        return false;
    }

    public void Adicionar(TKey chave, TValue valor)
    {
        if (_map.TryGetValue(chave, out var existente)) _ordem.Remove(existente);
        var node = new LinkedListNode<(TKey, TValue)>((chave, valor));
        _ordem.AddFirst(node);
        _map[chave] = node;
        while (_map.Count > _capacidade)
        {
            var ultimo = _ordem.Last!;
            _ordem.RemoveLast();
            _map.Remove(ultimo.Value.Chave);
        }
    }
}
