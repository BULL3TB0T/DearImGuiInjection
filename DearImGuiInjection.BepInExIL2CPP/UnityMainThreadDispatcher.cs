using BepInEx.Unity.IL2CPP.Utils.Collections;
using System;
using System.Collections;
using System.Collections.Generic;
using System.Text;
using UnityEngine;

namespace DearImGuiInjection.BepInExIL2CPP;

public class UnityMainThreadDispatcher : MonoBehaviour
{
    private static readonly Queue<Action> _executionQueue = new();

    private static UnityMainThreadDispatcher _instance;

    private void Awake()
    {
        _instance = this;
    }

    private void OnDestroy()
    {
        _instance = null;
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

    public static void Enqueue(IEnumerator action)
    {
        lock (_executionQueue)
        {
            _executionQueue.Enqueue(() =>
            {
                _instance.StartCoroutine(action.WrapToIl2Cpp());
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