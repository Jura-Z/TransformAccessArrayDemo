# TransformAccessArrayDemo

What's the fastest way to move a lot of transforms in Unity? 

Here we try to use a naive transform.SetPositionAndRotation + some raycasts to get a new position and that's around 32 msec to move 40k objects (22.4 msec) and cast 20k rays (9.6 msec).

And then we try to do the same (40k rotation/movement + 20k raycasts) in 0.120 msec in the main thread (2.5 msec on background jobs).

![Overview](Docs/Pictures/Difference.png)

## DecalMovement

This project demonstrates a few ways to implement moving 3d objects (casters) that project a decal (see [Decal Documentation in URP](https://docs.unity3d.com/Packages/com.unity.render-pipelines.universal@12.0/manual/renderer-feature-decal.html))
on the ground below them.

### Naive

A naive implementation could involve having an [agent](https://github.com/Jura-Z/TransformAccessArrayDemo/blob/main/TransformAccessArrayDemo/Assets/Scripts/DecalMovement/Naive/NaiveAgent.cs) do the following for each Update:
- Move and apply a new position/rotation using [Transform.SetPositionAndRotation](https://docs.unity3d.com/ScriptReference/Transform.SetPositionAndRotation.html). Code [lives here](https://github.com/Jura-Z/TransformAccessArrayDemo/blob/main/TransformAccessArrayDemo/Assets/Scripts/DecalMovement/Naive/NaiveAgent.cs#L81)
- Perform a [Physics.RaycastNonAlloc](https://docs.unity3d.com/ScriptReference/Physics.RaycastNonAlloc.html) from the new position to find where Decal must be positioned. Code [lives here](https://github.com/Jura-Z/TransformAccessArrayDemo/blob/main/TransformAccessArrayDemo/Assets/Scripts/DecalMovement/Naive/NaiveAgent.cs#L84)
- If there is a hit then position the decal with the same [Transform.SetPositionAndRotation](https://docs.unity3d.com/ScriptReference/Transform.SetPositionAndRotation.html). Code [lives here](https://github.com/Jura-Z/TransformAccessArrayDemo/blob/main/TransformAccessArrayDemo/Assets/Scripts/DecalMovement/Naive/NaiveAgent.cs#L89)

To control how many agents we have and how we spawn them - we have a [manager](https://github.com/Jura-Z/TransformAccessArrayDemo/blob/main/TransformAccessArrayDemo/Assets/Scripts/DecalMovement/Naive/NaiveManager.cs)
that uses [`ObjectPool` of agents](https://github.com/Jura-Z/TransformAccessArrayDemo/blob/main/TransformAccessArrayDemo/Assets/Scripts/DecalMovement/Naive/NaiveManager.cs#L65).

This way we're not calling `Instantiate`/`Destroy`, but just disabling objects on destroy, and enabling them on create. If and only if the pool is empty will the expensive call to `Instantiate` be made.

![Naive profiler's timeline](Docs/Pictures/Naive-Timeline.png)
![Naive profiler's hierarchy](Docs/Pictures/Naive-Hierarchy.png)

32 msec to move 20k casters + 20k decals??? Raycasts are 9.6 msec, movement code is 22.4 msec. But is it possible to make it faster?

### TransformAccessArray - what's that?

Each hierarchy in the root is a special object called `TransformHierarchy` that has an array of transforms in it. You even can control its capacity via [Transform.hierarchyCapacity](https://docs.unity3d.com/ScriptReference/Transform-hierarchyCapacity.html) - that doc also has a bit of technical details.

`TransformAccess` defines single transform, basically `TransformHierarchy` pointer + index inside of it.

And `TransformAccessArray` is an array of those `TransformAccess` objects that is ready to be processed in multithreaded way. 

> ??? Yes, that's right - **we can modify GameObject's Transforms via jobs**. And that's insanely fast!

Hierarchy is critical - it controls how jobs can be scheduled. Only one thread can modify one `TransformHierarchy`. Read only jobs don't have such limitation, [see Additional note on ReadOnly transform jobs](#additional-note-on-readonly-transform-jobs)

Also take a look at https://www.youtube.com/watch?v=W45-fsnPhJY&t=798 from amazing Ian Dundore (all his talks are great and must-see!).


### Implementation

So, how?

Let's say we decouple 'agent' to 'caster' and 'decal'.
In such a case the logic will be different:
[The manager](https://github.com/Jura-Z/TransformAccessArrayDemo/blob/main/TransformAccessArrayDemo/Assets/Scripts/DecalMovement/TransformAccessArrayWrapper/TransformAccessArrayManager.cs) would control a list of 'casters' and 'decals'.

Also, we need to use [jobs](https://unity.com/dots/packages#c-job-system) (multithreading) and [Burst](https://unity.com/dots/packages#burst-compiler) (special compiler for a C# subset).

To do the raycasts we'd use multithreaded [`RaycastCommand.ScheduleBatch`](https://docs.unity3d.com/ScriptReference/RaycastCommand.ScheduleBatch.html)

For every Update of the manager it would spawn a chain of jobs that depend on each other:
- it would [spawn a job](https://github.com/Jura-Z/TransformAccessArrayDemo/blob/main/TransformAccessArrayDemo/Assets/Scripts/DecalMovement/TransformAccessArrayWrapper/TransformAccessArrayManager.cs#L228) that changes the position of all casters.
- next job [would prepare RaycastCommands](https://github.com/Jura-Z/TransformAccessArrayDemo/blob/main/TransformAccessArrayDemo/Assets/Scripts/DecalMovement/TransformAccessArrayWrapper/TransformAccessArrayManager.cs#L241)
- next job that actually [does Raycasts in parallel](https://github.com/Jura-Z/TransformAccessArrayDemo/blob/main/TransformAccessArrayDemo/Assets/Scripts/DecalMovement/TransformAccessArrayWrapper/TransformAccessArrayManager.cs#L248)
- next job [positions decals on raycasts' hit position](https://github.com/Jura-Z/TransformAccessArrayDemo/blob/main/TransformAccessArrayDemo/Assets/Scripts/DecalMovement/TransformAccessArrayWrapper/TransformAccessArrayManager.cs#L253)
- job handle of the whole chain is memorized, to call it's `Complete` on [the beginning of the next Update](https://github.com/Jura-Z/TransformAccessArrayDemo/blob/main/TransformAccessArrayDemo/Assets/Scripts/DecalMovement/TransformAccessArrayWrapper/TransformAccessArrayManager.cs#L215).

Therefore, other than scheduling the jobs, `Update` takes almost no time on the main thread. In addition to that, 

MoveAgentsJob -> CommandsCreationJob -> RaycastCommand.ScheduleBatch -> SetPositionsJob are executed in parallel.

![TAA profiler's timeline. Without Burst](Docs/Pictures/TAA-NoBurst.png)

Just 2.37 msec spent on the main thread! Around 4 msec total. That's much faster.

Let's try enabling Burst:

![TAA profiler's timeline. With Burst](Docs/Pictures/TAA-Burst.png)

Unfortunately, this didn't change much.  Apparently, the movement code is not in our application's critical path anymore.  However, our performance is being decreased by something else.  Perhaps rendering is the cause of our performance drop.  On the other hand, Burst's cost of invoking jobs is almost the same as the performance benefits we observe from the fast compilation.  Remember, we are competing against il2cpp and not mono!

Ok, let's try to make this even faster.

### TransformAccessArray + correctly organized hierarchy

As I said hierarchy is critical - because it controls how jobs are scheduled.
Only one thread can process one `TransformHierarchy`. To demonstrate it, let's try the worst possible case which unfortunately is a case that occurs often.

#### Wrong hierarchy: all under one parent GameObject

If we create all agents under some parent GameObject like this:

```
Scene
  - Casters
    - caster1
    - caster2
    - caster...
    - caster342
  - Decals
    - decal1
    - decal2
    - decal...
    - decal342
```

We would kill all the performance.

![TAA profiler's timeline. With Burst, worst possible hierarchy](Docs/Pictures/TAA-SingleParent-Burst.png)

There is just one job that does all the read-write work, because there is only one `TransformHierarchy` per all casters and one per all decals.

*Note:* Read only jobs don't have such limitation, [see Additional note on ReadOnly transform jobs](#additional-note-on-readonly-transform-jobs)

#### Better hierarchy: all in the root

Currently we're creating all casters in the root, like:

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

![TAA profiler's timeline. With Burst](Docs/Pictures/TAA-Burst.png)

As you can see, we now have a much better picture however, we are still experiencing a lot of time spent on just scheduling.

To answer this I need to dig into Unity's source code. But since you don't have access to it, I try to explain it here:

Basically, for 40k objects in the root (20k casters, 20k decals) we have 40k `TransformHierarchy` that do some internal work, like for every `TransformHierarchy` that was used for scheduling a transform job (`IJobParallelForTransform`) - Unity engine marks them as 'potentially changed', by calling `DidScheduleTransformJob` and adding them to a special list, on main thread. That's not really expensive for few `TransformHierarchy`, but for 40k that's 2msec on my machine!

#### Optimal hierarchy: root buckets of 256

So, to improve the performance we need to reduce the amount of `TransformHierarchy`-s. For instance by creating one `TransformHierarchy` per 256 objects, like so:

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

This reduces the amount of `TransformHierarchy` from 40k to ~157 and we would reduce complexity of some internal algorithm that has O(`TransformHierarchy` count). Take a look:

![TAA profiler's timeline. With Burst, with correct hierarchy](Docs/Pictures/TAA-CorrectHierarchy-Burst.png)

We can now observe that the scheduler spends almost no time on the main thread. Total jobs completion takes ~2.5 msec.

That's for 20k objects and 20k decals!

![Overview](Docs/Pictures/Difference.png)

#### Additional note on ReadOnly transform jobs

There is an addition to Unity 2020.2 - [RunReadOnly](https://docs.unity3d.com/ScriptReference/Jobs.IJobParallelForTransformExtensions.RunReadOnly.html) and [ScheduleReadOnly](https://docs.unity3d.com/ScriptReference/Jobs.IJobParallelForTransformExtensions.ScheduleReadOnly.html) that actually ignores `TransformHierarchy` multithreading limitation and can parallelyze jobs differently - more evenly, because of the fact that they're not changing anything, so the order doesn't matter.

# Notes

## Profiling

All profiling here was done in a dedicated development build, il2cpp in the release mode. VSync disabled, rendering jobs enabled. Windowed + resizable window.

It is OK to profile inside Editor just to check the relative changes. Preferably use `Profiler (Standalone)` that will open a separate UMPE Editor instance.

However, please ensure that final performance observations are measured using a standalone build.

Burst for the build can be enabled/disabled in the Player settings -> Burst AOT Settings -> Enable Burst Compilation

## ScriptTemplates

There is a way to define per-project script templates, by creating `Assets/ScriptTemplates` folder. See https://github.com/Jura-Z/TransformAccessArrayDemo/tree/main/TransformAccessArrayDemo/Assets/ScriptTemplates


Whenever possible, MonoBehaviours in the Demo project are using `[SerializedField]` if they can - this should automate 'searching' for needed components in `Reset` in the Editor. 

Loading times are improved greatly through the means of loading scenes with serialized fields which is work that is offloaded from the main thread.

`Awake` and `Reset` are calling the same function called `SetReferences` that assign null fields. Then `Validate` (UNITY_EDITOR only) can contain any validation logic. For example, "We need a collider which must be marked as a trigger."

Therefore, in this case, the `Validate` function is a constant for the MonoBehaviour and helps us to see issues early on in the Editor.



