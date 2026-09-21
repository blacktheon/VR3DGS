using System.IO;
using UnityEditor.TestTools.TestRunner.Api;
using UnityEngine;

namespace SplatPreprocess.Tests
{
    public sealed class Stage1TestBridge : ICallbacks
    {
        string output;
        string summary;
        public static string Run(string label)
        {
            if (Path.GetFileName(label) != label) throw new System.ArgumentException("Expected a report filename");
            var callback = new Stage1TestBridge { output = Path.GetFullPath(Path.Combine(Application.dataPath, "../SplatData/validation/m1/", label + ".xml")) };
            Directory.CreateDirectory(Path.GetDirectoryName(callback.output));
            var api = ScriptableObject.CreateInstance<TestRunnerApi>();
            try
            {
                api.RegisterCallbacks(callback);
                api.Execute(new ExecutionSettings(new Filter { testMode = TestMode.EditMode, assemblyNames = new[] { "SplatPreprocess.EditMode.Tests" } }) { runSynchronously = true });
                return callback.summary ?? "Test run did not complete";
            }
            finally { api.UnregisterCallbacks(callback); Object.DestroyImmediate(api); }
        }
        public void RunStarted(ITestAdaptor testsToRun) { }
        public void TestStarted(ITestAdaptor test) { }
        public void TestFinished(ITestResultAdaptor result) { }
        public void RunFinished(ITestResultAdaptor result)
        {
            TestRunnerApi.SaveResultToFile(result, output);
            summary = $"Passed={result.PassCount}, Failed={result.FailCount}, Skipped={result.SkipCount}; {output}";
        }
    }
}
