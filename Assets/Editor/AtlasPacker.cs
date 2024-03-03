using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using UnityEngine;
using UnityEditor;
using Object = System.Object;

public class AtlasPacker : EditorWindow
{
    private string saveLocation = "/Textures/Packed_Atlas.png";
    private int blockSize = 256;  // Block size in pixels.
    private int atlasSizeInBlocks = VoxelData.TextureAtlasSizeInBlocks;
    private int atlasSize;

    private Object[] rawTextures = new Object[256];
    private List<Texture2D> sortedTextures = new List<Texture2D>();
    private Texture2D atlas;
    
    [MenuItem("Minecraft Clone/Atlas Packer")]
    public static void ShowWindow()
    {
        EditorWindow.GetWindow(typeof(AtlasPacker));
    }

    private void OnGUI()
    {
        atlasSize = blockSize * atlasSizeInBlocks;
        
        GUILayout.Label("Minecraft Clone Texture Atlas Packer", EditorStyles.boldLabel);

        saveLocation = EditorGUILayout.TextField("Save Location", saveLocation);
        blockSize = EditorGUILayout.IntField("Block Size", blockSize);
        atlasSizeInBlocks = EditorGUILayout.IntField("Atlas Size (in blocks)", atlasSizeInBlocks);
        
        if (GUILayout.Button("Load Textures"))
        {
            LoadTextures();
            PackAtlas();
        }
        
        if (GUILayout.Button("Clear Textures"))
        {
            atlas = new Texture2D(atlasSize, atlasSize);
            Debug.Log("Atlas Packer: Textures cleared.");
        }
        
        if (GUILayout.Button("Save Atlas"))
        {
            WriteAtlasToFile();
        }
        
        GUILayout.Label("Preview:", EditorStyles.boldLabel);
        GUILayout.Box(atlas, GUILayout.Width(this.position.width), GUILayout.Height(this.position.height));
    }

    private void LoadTextures()
    {
        sortedTextures.Clear();
        
        rawTextures = Resources.LoadAll("AtlasPacker", typeof(Texture2D));

        int index = 0;
        foreach (Object tex in rawTextures)
        {
            Texture2D t = (Texture2D)tex;
            if (t.width == blockSize && t.height == blockSize)
            {
                sortedTextures.Add(t);
            }
            else
            {
                Debug.Log($"Atlas Packer: {t.name} incorrect size. Texture not loaded.");
            }
            
            index++;
        }
        
        Debug.Log($"Atlas Packer: {sortedTextures.Count} successfully loaded.");
    }

    private void PackAtlas()
    {
        atlas = new Texture2D(atlasSize, atlasSize);
        Color[] pixels = new Color[atlasSize * atlasSize];

        for (int x = 0; x < atlasSize; x++)
        {
            for (int y = 0; y < atlasSize; y++)
            {
                // Get the current block that we're looking at.
                int currentBlockX = x / blockSize;
                int currentBlockY = y / blockSize;

                //                   rowIndex                 +  columnIndex
                int index = currentBlockY * atlasSizeInBlocks + currentBlockX;

                if (index < sortedTextures.Count)
                    pixels[(atlasSize - y - 1) * atlasSize + x] = sortedTextures[index].GetPixel(x, blockSize - y - 1);
                else
                    pixels[(atlasSize - y - 1) * atlasSize + x] = new Color(0f, 0f, 0f, 0f);
            }
        }
        
        atlas.SetPixels(pixels);
        atlas.Apply();
        Debug.Log("Atlas Packer: Texture Atlas Created.");
    }

    private void WriteAtlasToFile()
    {
        byte[] bytes = atlas.EncodeToPNG();
        try
        {
            string path = Application.dataPath + saveLocation;
            CreateDirectory(path);
            File.WriteAllBytes(path, bytes);
            AssetDatabase.Refresh();  // Refresh Unity's asset database.
            Debug.Log($"Atlas Packer: Atlas saved to {saveLocation}");
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
