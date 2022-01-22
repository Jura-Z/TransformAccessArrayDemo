# TransformAccessArrayDemo

## DecalMovement

This project demonstrates a few ways to implement moving 3d objects (casters) that project some decal (see [Decal Documentation in URP](https://docs.unity3d.com/Packages/com.unity.render-pipelines.universal@12.0/manual/renderer-feature-decal.html))
on the ground below them.

### Naive

To do so in a naive way you could have an [agent](https://github.com/Jura-Z/TransformAccessArrayDemo/blob/main/TransformAccessArrayDemo/Assets/Scripts/DecalMovement/Naive/NaiveAgent.cs)
that every Update:
- moves and applies new position/rotation using [Transform.SetPositionAndRotation](https://docs.unity3d.com/ScriptReference/Transform.SetPositionAndRotation.html). Code [lives here](https://github.com/Jura-Z/TransformAccessArrayDemo/blob/main/TransformAccessArrayDemo/Assets/Scripts/DecalMovement/Naive/NaiveAgent.cs#L81)
- does [Physics.RaycastNonAlloc](https://docs.unity3d.com/ScriptReference/Physics.RaycastNonAlloc.html) from the new position to find where Decal must be positioned. Code [lives here](https://github.com/Jura-Z/TransformAccessArrayDemo/blob/main/TransformAccessArrayDemo/Assets/Scripts/DecalMovement/Naive/NaiveAgent.cs#L84)
- if there is a hit - positions the decal with the same [Transform.SetPositionAndRotation](https://docs.unity3d.com/ScriptReference/Transform.SetPositionAndRotation.html). Code [lives here](https://github.com/Jura-Z/TransformAccessArrayDemo/blob/main/TransformAccessArrayDemo/Assets/Scripts/DecalMovement/Naive/NaiveAgent.cs#L89)

To control how many agents we have and how do we spawn them - we have a [manager](https://github.com/Jura-Z/TransformAccessArrayDemo/blob/main/TransformAccessArrayDemo/Assets/Scripts/DecalMovement/Naive/NaiveManager.cs)
that uses [`ObjectPool` of agents](https://github.com/Jura-Z/TransformAccessArrayDemo/blob/main/TransformAccessArrayDemo/Assets/Scripts/DecalMovement/Naive/NaiveManager.cs#L65).

This way we're not calling `Instantiate`/`Destroy`, but just disabling objects on destroy, and enabling them on create. And only if the pool is empty - it calls expensive `Instantiate`.

![Naive profiler's timeline](Docs/Pictures/Naive-Timeline.png)
![Naive profiler's hierarchy](Docs/Pictures/Naive-Hierarchy.png)

23 msec to move 20k casters + decals… But is it possible to make it faster?

### TransformAccessArray

A much faster way of implementing this would be to use [jobs](https://unity.com/dots/packages#c-job-system) (multithreading) and [Burst](https://unity.com/dots/packages#burst-compiler) (special compiler for a C# subset).

Let's say we decouple 'agent' to 'caster' and 'decal'.
The logic will be different in such case:
[The manager](https://github.com/Jura-Z/TransformAccessArrayDemo/blob/main/TransformAccessArrayDemo/Assets/Scripts/DecalMovement/TransformAccessArrayWrapper/TransformAccessArrayManager.cs) would control a list of 'casters' and 'decals'.

For every Update of the manager it would spawn a chain of jobs that depend on each other:
- it would [spawn a job](https://github.com/Jura-Z/TransformAccessArrayDemo/blob/main/TransformAccessArrayDemo/Assets/Scripts/DecalMovement/TransformAccessArrayWrapper/TransformAccessArrayManager.cs#L228) that changes the position of all casters.
- next job [would prepare RaycastCommands](https://github.com/Jura-Z/TransformAccessArrayDemo/blob/main/TransformAccessArrayDemo/Assets/Scripts/DecalMovement/TransformAccessArrayWrapper/TransformAccessArrayManager.cs#L241)
- next job that actually [does Raycasts in parallel](https://github.com/Jura-Z/TransformAccessArrayDemo/blob/main/TransformAccessArrayDemo/Assets/Scripts/DecalMovement/TransformAccessArrayWrapper/TransformAccessArrayManager.cs#L248)
- next job [positions decals on raycasts' hit position](https://github.com/Jura-Z/TransformAccessArrayDemo/blob/main/TransformAccessArrayDemo/Assets/Scripts/DecalMovement/TransformAccessArrayWrapper/TransformAccessArrayManager.cs#L253)
- job handle of the whole chain is memorized, to call it's `Complete` on [the beginning of the next Update](https://github.com/Jura-Z/TransformAccessArrayDemo/blob/main/TransformAccessArrayDemo/Assets/Scripts/DecalMovement/TransformAccessArrayWrapper/TransformAccessArrayManager.cs#L215).

So in such a way, `Update` takes almost no time on the main thread, other than scheduling the jobs, and 

MoveAgentsJob -> CommandsCreationJob -> RaycastCommand.ScheduleBatch -> SetPositionsJob are executed in parallel.

![TAA profiler's timeline. Without Burst](Docs/Pictures/TAA-NoBurst.png)

Just 2.37 msec spent on the main thread! Around 4 msec total. That's much faster.

Let's try enabling Burst:

![TAA profiler's timeline. With Burst](Docs/Pictures/TAA-Burst.png)

Hm, this didn't change much. Apparently movement code is not in the critical path of our application anymore, so we're slowed down by something else, like rendering.
Or Burst's cost of invoking jobs is almost the same as benefits from the fast compilation. Remember - we're competing against il2cpp, not mono!

Ok, let's try to make this even faster.

### TransformAccessArray + correctly organized hierarchy

Before we were just creating Transforms in the root. But if we try to create transforms in a buckets of 256 then we can speed up jobs on such transforms, because this empty parent would speed up whole bucket.

Before

```
Scene
  - caster1
  - caster2
  - caster...
  - caster342
  - decal1
  - decal2
  - decal...
  - decal342
```

Now
```
Scene
  - parentCaster1
    - caster1
    - caster2
    - caster...
    - caster256
  - parentCaster2
    - caster257
    - caster258
    - caster...
    - caster342
  - parentDecal1
    - decal1
    - decal2
    - decal...
    - decal256
  - parentDecal2
    - decal257
    - decal258
    - decal...
    - decal342
```

![TAA profiler's timeline. With Burst, with correct hierarchy](Docs/Pictures/TAA-CorrectHierarchy-Burst.png)

Now we almost cannot see the scheduler on the main thread. Total jobs completion takes ~2.5 msec.

That's for 20k objects!

![Overview](Docs/Pictures/Difference.png)

# More tech details about TransfromAccessArray

TODO

# Notes

## Profiling

All profiling here was done in a dedicated development build, il2cpp in the release mode. VSync disabled, rendering jobs enabled. Windowed + resizable window.

It is OK to profile inside Editor just to check the relative changes. Preferably use `Profiler (Standalone)` that will open a separate UMPE Editor instance.

But please, don't profile final performance without making a build!

Burst for the build can be enabled/disabled in the Player settings -> Burst AOT Settings -> Enable Burst Compilation

## ScriptTemplates

There is a way to define per-project script templates, by creating `Assets/ScriptTemplates` folder. See https://github.com/Jura-Z/TransformAccessArrayDemo/tree/main/TransformAccessArrayDemo/Assets/ScriptTemplates

All MonoBehaviours in the Demo project are using `[SerializedField]` if they can - this should automate 'searching' for needed components in `Reset` in the Editor. 

Loading of scenes with serialized fields is offloaded from the main thread so in such a way we make loading faster.

`Awake` and `Reset` are calling the same function called `SetReferences` that assign null fields. Then `Validate` (UNITY_EDITOR only) can contain any validation logic, like 'we need collider, that must be marked as a trigger'.

So in this case `Validate` function is a contract for the MonoBehaviour and helps see issues early in the Editor.



