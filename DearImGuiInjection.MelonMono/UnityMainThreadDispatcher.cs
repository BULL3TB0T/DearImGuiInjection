using MelonLoader;
using System;
using System.Collections;
using System.Collections.Generic;
using UnityEngine;

namespace DearImGuiInjection.MelonMono;

public class UnityMainThreadDispatcher : MonoBehaviour
{
    private static readonly Queue<Action> _executionQueue = new();

    private void OnDestroy()
    {
        lock (_executionQueue) _executionQueue.Clear();
    }

    public void Update()
    {
        lock (_executionQueue)
        {
            while (_executionQueue.Count > 0)
                _executionQueue.Dequeue().Invoke();
        }
    }

    public static void Enqueue(IEnumerator routine)
    {
        lock (_executionQueue)
        {
            _executionQueue.Enqueue(() =>
            {
                MelonCoroutines.Start(routine);
            });
        }
    }

    public static void Enqueue(Action action) => Enqueue(ActionWrapper(action));

    public static IEnumerator ActionWrapper(Action action)
    {
        action();
        yield break;
    }
}
