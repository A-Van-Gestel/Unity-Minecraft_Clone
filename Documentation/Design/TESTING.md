# Unity Test Framework Implementation Guide

This document outlines the architectural requirements and code generation strategies for implementing NUnit testing in this voxel engine project.

## Assembly Definition Migration

All runtime scripts currently reside within the predefined `Assembly-CSharp.dll`. Because test assemblies cannot reference predefined assemblies, a structural refactor is required before testing can begin.

### 1. The Runtime Assembly

An Assembly Definition file must be created to encapsulate the core game logic:
**Path:** `Assets/Scripts/MinecraftClone.Runtime.asmdef`
```json
{
    "name": "MinecraftClone.Runtime",
    "rootNamespace": "",
    "references": [
        "Unity.Mathematics",
        "Unity.Burst",
        "Unity.Collections"
    ],
    "includePlatforms": [],
    "excludePlatforms": [],
    "allowUnsafeCode": true,
    "overrideReferences": false,
    "precompiledReferences": [],
    "autoReferenced": true,
    "defineConstraints": [],
    "versionDefines": [],
    "noEngineReferences": false
}
```

### 2. The Test Assembly

A dedicated Assembly Definition file must be created to house the test suite:
**Path:** `Assets/Scripts/Tests/EditMode/MinecraftClone.Tests.asmdef`
```json
{
    "name": "MinecraftClone.Tests",
    "rootNamespace": "",
    "references": [
        "UnityEngine.TestRunner",
        "UnityEditor.TestRunner",
        "MinecraftClone.Runtime"
    ],
    "includePlatforms": [
        "Editor"
    ],
    "excludePlatforms": [],
    "allowUnsafeCode": false,
    "overrideReferences": true,
    "precompiledReferences": [
        "nunit.framework.dll"
    ],
    "autoReferenced": false,
    "defineConstraints": [
        "UNITY_INCLUDE_TESTS"
    ],
    "versionDefines": [],
    "noEngineReferences": false
}
```

## Dependency Inversion (Mocking)

Testing core systems (like `VoxelRigidbody` physics sweeps) mathematically requires decoupling them from heavy singletons like `World.Instance`, which would otherwise attempt to generate entire chunk meshes during a physics unit test.

### Step 1: Interface Definition
Extract environment dependencies into interfaces. For example:
```csharp
public interface IVoxelCollisionProvider
{
    bool CheckForCollision(Vector3 pos);
}
```

### Step 2: Production Implementation
Implement the interface in the singleton:
```csharp
public class World : MonoBehaviour, IVoxelCollisionProvider
{
    // ... existing logic ...
}
```

### Step 3: Injection
Modify dependent classes to accept the interface (defaulting to the singleton for backward compatibility in production scenes):
```csharp
public class VoxelRigidbody : MonoBehaviour
{
    public IVoxelCollisionProvider CollisionProvider { get; set; }

    private void Start()
    {
        CollisionProvider ??= World.Instance;
    }
}
```

## Example Test Suite: VoxelRigidbody

With the dependencies inverted, test suites can inject a lightweight `MockCollisionProvider` that fakes a mathematical block grid.

**Path:** `Assets/Scripts/Tests/EditMode/VoxelRigidbodyTests.cs`
```csharp
using NUnit.Framework;
using UnityEngine;
using System.Collections.Generic;

public class VoxelRigidbodyTests
{
    private class MockCollisionProvider : IVoxelCollisionProvider
    {
        public HashSet<Vector3Int> SolidBlocks = new HashSet<Vector3Int>();

        public bool CheckForCollision(Vector3 pos)
        {
            Vector3Int gridPos = new Vector3Int(Mathf.FloorToInt(pos.x), Mathf.FloorToInt(pos.y), Mathf.FloorToInt(pos.z));
            return SolidBlocks.Contains(gridPos);
        }
    }

    [Test]
    public void HorizontalSweep_HitsFlatWall_ReturnsTrue()
    {
        // 1. Arrange
        GameObject go = new GameObject();
        go.transform.position = new Vector3(0.5f, 0f, 0.5f);
        
        VoxelRigidbody rb = go.AddComponent<VoxelRigidbody>();
        rb.collisionWidthX = 0.8f;
        rb.collisionDepthZ = 0.8f;
        rb.collisionPadding = 0.0f; 
        
        MockCollisionProvider mockWorld = new MockCollisionProvider();
        mockWorld.SolidBlocks.Add(new Vector3Int(1, 0, 0)); // Solid wall at X=1
        rb.CollisionProvider = mockWorld;

        // 2. Act & Assert
        // Moving 0.09 on X: The right edge of the player (0.5 + 0.4 = 0.9) will reach 0.99. No collision.
        Assert.IsFalse(rb.CheckHorizontalCollisionTestHelper(0.09f, 0f));

        // Moving 0.11 on X: The right edge (0.9) + 0.11 = 1.01. This penetrates the block at X=1. Collision!
        Assert.IsTrue(rb.CheckHorizontalCollisionTestHelper(0.11f, 0f));

        // 3. Cleanup
        Object.DestroyImmediate(go);
    }
}
```
