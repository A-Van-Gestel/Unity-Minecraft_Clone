using System.Collections.Generic;
using UnityEditor;
using UnityEngine;

namespace Helpers
{
    public static class ScriptableObjectHelpers
    {
        /// <summary> Returns all ScriptableObjects at a certain path.</summary>
        /// <param name="path"></param>
        /// <typeparam name="T"></typeparam>
        public static List<T> FindAll<T>(string path = "") where T : ScriptableObject
        {
            List<T> scripts = new List<T>();
            string searchFilter = $"t:{typeof(T).Name}";
            string[] soNames = path == ""
                ? AssetDatabase.FindAssets(searchFilter)
                : AssetDatabase.FindAssets(searchFilter, new[] { path });
            foreach (string soName in soNames)
            {
                string soPath = AssetDatabase.GUIDToAssetPath(soName);
                T script = AssetDatabase.LoadAssetAtPath<T>(soPath);
                if (script == null)
                    continue;
                scripts.Add(script);
            }

            return scripts;
        }
    }
}