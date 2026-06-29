using System;
using System.Collections.Generic;
using System.Reflection;
using Attributes;
using Data;
using UnityEditor;
using UnityEngine;

namespace Editor.PropertyDrawers
{
    [CustomPropertyDrawer(typeof(BlockIDAttribute))]
    public class BlockIDDrawer : PropertyDrawer
    {
        private string[] _names;
        private ushort[] _values;

        private void Initialize()
        {
            if (_names != null) return;

            var constants = new List<FieldInfo>();
            var fields = typeof(BlockIDs).GetFields(BindingFlags.Public | BindingFlags.Static | BindingFlags.FlattenHierarchy);

            foreach (var field in fields)
            {
                if (field.IsLiteral && field.FieldType == typeof(ushort))
                {
                    constants.Add(field);
                }
            }

            _names = new string[constants.Count];
            _values = new ushort[constants.Count];

            for (int i = 0; i < constants.Count; i++)
            {
                _names[i] = constants[i].Name;
                _values[i] = (ushort)constants[i].GetValue(null);
            }
        }

        public override void OnGUI(Rect position, SerializedProperty property, GUIContent label)
        {
            Initialize();

            ushort currentValue = (ushort)property.intValue;
            int selectedIndex = Array.IndexOf(_values, currentValue);
            bool valueFound = selectedIndex >= 0;

            string[] displayNames = _names;
            ushort[] displayValues = _values;

            if (!valueFound)
            {
                selectedIndex = 0;
                displayNames = new string[_names.Length + 1];
                displayValues = new ushort[_values.Length + 1];
                displayNames[0] = $"NOT FOUND: {currentValue}";
                displayValues[0] = currentValue;
                Array.Copy(_names, 0, displayNames, 1, _names.Length);
                Array.Copy(_values, 0, displayValues, 1, _values.Length);

                EditorGUI.DrawRect(position, new Color(1f, 1f, 0f, 0.1f));
            }

            EditorGUI.BeginChangeCheck();
            int newIndex = EditorGUI.Popup(position, label.text, selectedIndex, displayNames);
            if (EditorGUI.EndChangeCheck())
            {
                property.intValue = displayValues[newIndex];
                property.serializedObject.ApplyModifiedProperties();
            }
        }
    }
}
