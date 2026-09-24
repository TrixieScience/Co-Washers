using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace DragNWashCoop;

/// <summary>
/// Same-build scene paths and active-dragon-relative paths survive different Unity instance IDs.
/// A scene root is named by its name and which of the same-named roots it is: the game creates
/// short-lived objects at the root (e.g. one per sound played), which would shift plain root indices
/// differently on each machine. Below the root, sibling indices are stable (runtime children are added last).
/// </summary>
internal static class EntityIds
{
    internal static string For(Component component) => For(component.gameObject);

    internal static string For(GameObject gameObject)
    {
        Transform? dragonRoot = null;
        if (WalkNWashSceneState.TryGetActiveDragon(out var dragon)) dragonRoot = dragon.gameObject.transform;
        var t = gameObject.transform;
        var indices = new Stack<int>();
        while (t.parent != null && t != dragonRoot)
        {
            indices.Push(t.GetSiblingIndex());
            t = t.parent;
        }
        if (t == dragonRoot) return "@dragon/" + string.Join("/", indices);
        var scene = SceneManager.GetActiveScene();
        int occurrence = 0;
        foreach (var root in scene.GetRootGameObjects())
        {
            if (root == t.gameObject) break;
            if (root.name == t.name) occurrence++;
        }
        return scene.name + "/" + Uri.EscapeDataString(t.name) + "#" + occurrence + "/" + string.Join("/", indices);
    }

    internal static GameObject? Find(string id)
    {
        if (string.IsNullOrEmpty(id) || id.Length > 1024) return null;
        string[] parts = id.Split('/');
        Transform? current = null;
        int start;
        if (parts[0] == "@dragon")
        {
            if (!WalkNWashSceneState.TryGetActiveDragon(out var dragon)) return null;
            current = dragon.gameObject.transform;
            start = 1;
        }
        else
        {
            var scene = SceneManager.GetActiveScene();
            if (parts.Length < 2 || parts[0] != scene.name) return null;
            int hash = parts[1].LastIndexOf('#');
            if (hash < 0 || !int.TryParse(parts[1].Substring(hash + 1), out int occurrence) || occurrence < 0) return null;
            string name = Uri.UnescapeDataString(parts[1].Substring(0, hash));
            foreach (var root in scene.GetRootGameObjects())
                if (root.name == name && occurrence-- == 0) { current = root.transform; break; }
            if (current == null) return null;
            start = 2;
        }
        for (int i = start; i < parts.Length; i++)
        {
            if (parts[i] == "") continue;
            if (!int.TryParse(parts[i], out int index) || index < 0 || index >= current.childCount) return null;
            current = current.GetChild(index);
        }
        return current.gameObject;
    }
}
