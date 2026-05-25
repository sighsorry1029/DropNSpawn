using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace DropNSpawn;

internal static class SceneTraversalSupport
{
    internal static void VisitActiveLoadedSceneGameObjects(Action<GameObject> visitor)
    {
        if (visitor == null)
        {
            return;
        }

        List<Transform> pendingTransforms = new();
        int sceneCount = SceneManager.sceneCount;
        for (int sceneIndex = 0; sceneIndex < sceneCount; sceneIndex++)
        {
            Scene scene = SceneManager.GetSceneAt(sceneIndex);
            if (!scene.isLoaded)
            {
                continue;
            }

            foreach (GameObject rootObject in scene.GetRootGameObjects())
            {
                if (rootObject == null || !rootObject.activeInHierarchy)
                {
                    continue;
                }

                TraverseHierarchy(rootObject.transform, transform =>
                {
                    GameObject gameObject = transform.gameObject;
                    if (gameObject != null && gameObject.activeInHierarchy)
                    {
                        visitor(gameObject);
                    }
                }, pendingTransforms);
            }
        }
    }

    internal static void TraverseHierarchy(Transform? root, Action<Transform> visitor)
    {
        TraverseHierarchy(root, visitor, new List<Transform>());
    }

    private static void TraverseHierarchy(Transform? root, Action<Transform> visitor, List<Transform> pendingTransforms)
    {
        if (root == null || visitor == null)
        {
            return;
        }

        pendingTransforms.Clear();
        pendingTransforms.Add(root);
        while (pendingTransforms.Count > 0)
        {
            int lastIndex = pendingTransforms.Count - 1;
            Transform transform = pendingTransforms[lastIndex];
            pendingTransforms.RemoveAt(lastIndex);
            if (transform == null)
            {
                continue;
            }

            visitor(transform);
            for (int childIndex = 0; childIndex < transform.childCount; childIndex++)
            {
                Transform child = transform.GetChild(childIndex);
                if (child != null)
                {
                    pendingTransforms.Add(child);
                }
            }
        }
    }
}
