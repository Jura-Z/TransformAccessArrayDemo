using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using UnityEditor;
using UnityEditor.TestTools;
using UnityEngine;
using Debug = UnityEngine.Debug;

[assembly:TestPlayerBuildModifier(typeof(NaiveTestsBuildModifier))]

// To know more about this see QA your code: The new Unity Test Framework - Unite Copenhagen
// https://www.youtube.com/watch?v=wTiF2D0_vKA&t=1208s
public class NaiveTestsBuildModifier : ITestPlayerBuildModifier
{
    public BuildPlayerOptions ModifyOptions(BuildPlayerOptions playerOptions)
    {
        var guids = AssetDatabase.FindAssets("t:scene TestScene");

        foreach (var guid in guids)
        {
            var path = AssetDatabase.GUIDToAssetPath(guid);

            if (playerOptions.scenes.Contains(path) == false)
                playerOptions.scenes = playerOptions.scenes.Append(path).ToArray();
        }

        return playerOptions;
    }
}
