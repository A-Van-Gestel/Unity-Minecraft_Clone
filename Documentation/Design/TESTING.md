# Unity Test Framework Implementation Guide

> Status: **Reference only — migration rejected (evaluated 2026-07-02).** The custom editor
> validation framework (`Assets/Editor/Validation/`, six suites + three standalone test files —
> see `VALIDATION_SUITE_COVERAGE_ROADMAP.md` for the coverage map) is the project's testing system
> of record. Migrating it to the Unity Test Framework was evaluated and rejected as wasted effort:
>
> 1. **The asmdef restructure is the real cost.** Test assemblies cannot reference the predefined
     > `Assembly-CSharp`, so *any* UTF test touching game code first requires the runtime-assembly
     > migration in §1 below — a restructure that ripples into the `dotnet build
>    "Assembly-CSharp.csproj"` verification loop, the editor validation assembly, and Burst AOT
     > settings, and which the project has deliberately not done.
> 2. **UTF would replace the wrong 95%.** The suites' value is the scenarios, oracles, test worlds,
     > and simulators — UTF replaces only the ~90-line runner scaffold that `VS-1`
     > (`PERFORMANCE_IMPROVEMENTS_REPORT.md`) extracts into a shared runner anyway, while forcing a
     > verdict-parity re-verification of every suite for no behavioral gain.
> 3. **UTF does not enable `dotnet test`.** Unity code executes only editor-hosted; UTF's CLI story
     > (`-batchmode -runTests`) is the same class of entry point as VS-2's planned
     > `-executeMethod` one. This limitation is Unity's, not the framework choice's.
> 4. **The operational gaps close without UTF.** CI/headless runs, NUnit-format XML results, and
     > coverage reports all land on the custom runner via the VS-2 extensions — the required
     > packages (`com.unity.test-framework`, `com.unity.testtools.codecoverage`,
     > `com.unity.test-framework.performance`) are already installed via
     > `com.unity.feature.development`.
>
> **Conditional future role:** if the asmdef split is ever done *on its own merits* (compile-time
> isolation, enforced layering), adopt UTF additively — the pure-function tests
> (`FastNoiseLiteTests`, `VoxelMetadataUtilityTests`, `ChunkRelativePositionTests`, and the NS-5/
> NS-6 roadmap suites) as plain `[Test]`s, plus one thin `[Test]` wrapper per suite asserting the
> VS-1 runner's headless result — never a scenario migration. The sections below remain the
> correct recipe for that scenario.

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
