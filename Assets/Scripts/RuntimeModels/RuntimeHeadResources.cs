using System.Collections.Generic;
using UnityEngine;

namespace G4Runtime
{
    public sealed class RuntimeHeadResources : MonoBehaviour
    {
        readonly List<Object> resources = new List<Object>();
        public T Own<T>(T resource) where T : Object { resources.Add(resource); return resource; }
        public void Release()
        {
            foreach (var resource in resources)
            {
                if (resource == null) continue;
                if (Application.isPlaying) Destroy(resource); else DestroyImmediate(resource);
            }
            resources.Clear();
        }
        void OnDestroy() => Release();
    }
}
