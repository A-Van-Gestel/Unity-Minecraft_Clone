using System;
using System.Collections.Generic;
using System.IO;
using UnityEditor;
using UnityEngine;

namespace Editor.ProjectUtilities
{
    public class AtlasPacker : EditorWindow
    {
        private string _saveLocation = "/Textures/Packed_Atlas.png";
        private int _blockSize = 256; // Block size in pixels.
        private int _atlasSizeInBlocks = VoxelData.TextureAtlasSizeInBlocks;
        private int _atlasSize;

        private Texture2D[] _rawTextures = Array.Empty<Texture2D>();
        private readonly List<Texture2D> _sortedTextures = new List<Texture2D>();
        private Texture2D _atlas;

        [MenuItem("Minecraft Clone/Atlas Packer")]
        public static void ShowWindow()
        {
            GetWindow(typeof(AtlasPacker));
        }

        private void OnGUI()
        {
            _atlasSize = _blockSize * _atlasSizeInBlocks;

            GUILayout.Label("Minecraft Clone Texture Atlas Packer", EditorStyles.boldLabel);

            _saveLocation = EditorGUILayout.TextField("Save Location", _saveLocation);
            _blockSize = EditorGUILayout.IntField("Block Size", _blockSize);
            _atlasSizeInBlocks = EditorGUILayout.IntField("Atlas Size (in blocks)", _atlasSizeInBlocks);

            if (GUILayout.Button("Load Textures"))
            {
                LoadTextures();
                PackAtlas();
            }

            if (GUILayout.Button("Clear Textures"))
            {
                _atlas = new Texture2D(_atlasSize, _atlasSize);
                Debug.Log("Atlas Packer: Textures cleared.");
            }

            if (GUILayout.Button("Save Atlas"))
            {
                WriteAtlasToFile();
            }

            GUILayout.Label("Preview:", EditorStyles.boldLabel);
            GUILayout.Box(_atlas, GUILayout.Width(this.position.width), GUILayout.Height(this.position.height));
        }

        private void LoadTextures()
        {
            _sortedTextures.Clear();

            _rawTextures = Resources.LoadAll<Texture2D>("AtlasPacker");

            foreach (Texture2D t in _rawTextures)
            {
                if (t.width == _blockSize && t.height == _blockSize)
                {
                    _sortedTextures.Add(t);
                }
                else
                {
                    Debug.Log($"Atlas Packer: {t.name} incorrect size. Texture not loaded.");
                }
            }

            Debug.Log($"Atlas Packer: {_sortedTextures.Count} successfully loaded.");
        }

        private void PackAtlas()
        {
            _atlas = new Texture2D(_atlasSize, _atlasSize);
            Color[] pixels = new Color[_atlasSize * _atlasSize];

            for (int x = 0; x < _atlasSize; x++)
            {
                for (int y = 0; y < _atlasSize; y++)
                {
                    // Get the current block that we're looking at.
                    int currentBlockX = x / _blockSize;
                    int currentBlockY = y / _blockSize;

                    //                   rowIndex                 +  columnIndex
                    int index = currentBlockY * _atlasSizeInBlocks + currentBlockX;

                    if (index < _sortedTextures.Count)
                        pixels[(_atlasSize - y - 1) * _atlasSize + x] = _sortedTextures[index].GetPixel(x, _blockSize - y - 1);
                    else
                        pixels[(_atlasSize - y - 1) * _atlasSize + x] = new Color(0f, 0f, 0f, 0f);
                }
            }

            _atlas.SetPixels(pixels);
            _atlas.Apply();
            Debug.Log("Atlas Packer: Texture Atlas Created.");
        }

        private void WriteAtlasToFile()
        {
            byte[] bytes = _atlas.EncodeToPNG();
            try
            {
                string path = Application.dataPath + _saveLocation;
                CreateDirectory(path);
                File.WriteAllBytes(path, bytes);
                AssetDatabase.Refresh(); // Refresh Unity's asset database.
                Debug.Log($"Atlas Packer: Atlas saved to {_saveLocation}");
            }
            catch (Exception e)
            {
                Debug.Log("Atlas Packer: Couldn't save atlas to file." + e);
            }
        }

        private static void CreateDirectory(string path)
        {
            string fileName = Path.GetFileName(path);
            string directoryPath = path[..path.LastIndexOf(fileName, StringComparison.Ordinal)];
            Directory.CreateDirectory(directoryPath);
        }
    }
}
