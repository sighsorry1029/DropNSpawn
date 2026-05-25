using System;

namespace DropNSpawn;

internal sealed class RingBufferQueue<T>
{
    private T[] _buffer;
    private int _head;
    private int _count;

    internal RingBufferQueue(int capacity = 16)
    {
        _buffer = new T[Math.Max(4, capacity)];
    }

    internal int Count => _count;

    internal void Enqueue(T item)
    {
        EnsureCapacity(_count + 1);
        int tail = (_head + _count) % _buffer.Length;
        _buffer[tail] = item;
        _count++;
    }

    internal bool TryPeek(out T item)
    {
        if (_count == 0)
        {
            item = default!;
            return false;
        }

        item = _buffer[_head];
        return true;
    }

    internal bool TryDequeue(out T item)
    {
        if (_count == 0)
        {
            item = default!;
            return false;
        }

        item = _buffer[_head];
        _buffer[_head] = default!;
        _head = (_head + 1) % _buffer.Length;
        _count--;
        if (_count == 0)
        {
            _head = 0;
        }

        return true;
    }

    internal void Clear()
    {
        if (_count > 0)
        {
            for (int index = 0; index < _count; index++)
            {
                _buffer[(_head + index) % _buffer.Length] = default!;
            }
        }

        _head = 0;
        _count = 0;
    }

    private void EnsureCapacity(int required)
    {
        if (required <= _buffer.Length)
        {
            return;
        }

        int newCapacity = _buffer.Length * 2;
        while (newCapacity < required)
        {
            newCapacity *= 2;
        }

        T[] newBuffer = new T[newCapacity];
        for (int index = 0; index < _count; index++)
        {
            newBuffer[index] = _buffer[(_head + index) % _buffer.Length];
        }

        _buffer = newBuffer;
        _head = 0;
    }
}
