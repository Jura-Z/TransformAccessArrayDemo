using System.Collections;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.SceneManagement;
using UnityEngine.TestTools;
using Assert = UnityEngine.Assertions.Assert;
using Random = UnityEngine.Random;

namespace TransformAccessArrayDemo.Tests
{
    public class TransformAccessArrayWrapperTests
    {
        [OneTimeSetUp]
        public void OneTimeSetup()
        {
            const string sceneName = "TestScene.TransformAccessArray";
#if UNITY_EDITOR
            var guids = UnityEditor.AssetDatabase.FindAssets("t:Scene " + sceneName);
            var scenePath = UnityEditor.AssetDatabase.GUIDToAssetPath(guids[0]);
            UnityEditor.SceneManagement.EditorSceneManager.LoadSceneInPlayMode(scenePath, new LoadSceneParameters {loadSceneMode = LoadSceneMode.Single});
#else
            SceneManager.LoadScene(sceneName);
#endif
        }

        [UnityTest]
        public IEnumerator TransformAccessArrayTestsChangeCount()
        {
            var manager = GameObject.FindObjectOfType<TransformAccessArrayManager>();

            Assert.IsNotNull(manager, $"Cannot find {nameof(TransformAccessArrayManager)}");
            yield return null;

            manager.Count = 0;
            yield return null;

            manager.Count = 100;
            yield return null;

            manager.Count = 10000;
            yield return null;

            manager.Count = 40;
            yield return null;

            manager.Count = 1;
            yield return null;

            manager.Count = 1;
            yield return null;

            manager.Count = 0;
            yield return null;
        }

        private static int[] s_RndSeedValues = { 1, 1423412, 312423469, -123123123 };

        [UnityTest]
        public IEnumerator TransformAccessArrayTestsChangeCountRandom([ValueSource(nameof(s_RndSeedValues))] int seed)
        {
            var manager = GameObject.FindObjectOfType<TransformAccessArrayManager>();

            Assert.IsNotNull(manager, $"Cannot find {nameof(TransformAccessArrayManager)}");
            yield return null;

            Random.InitState(seed);

            for (var i = 0; i < 100; i++)
            {
                var random = Random.Range(-100, 5100);
                if (random < 0)
                    random = 0;
                if (random > 5000)
                    random = 5000;

                manager.Count = random;
                yield return null;
            }
        }
    }
}
