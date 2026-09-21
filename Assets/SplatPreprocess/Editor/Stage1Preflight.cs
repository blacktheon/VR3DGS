using System.Collections.Generic;
using System.Linq;
using Gsplat;
using Gsplat.Editor;
using UnityEditor;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.Universal;

namespace SplatPreprocess.Editor
{
    public static class Stage1Preflight
    {
        public static IReadOnlyList<string> Check()
        {
            var messages = new List<string>();
            if (!(GraphicsSettings.currentRenderPipeline is UniversalRenderPipelineAsset pipeline)) messages.Add("Select a URP pipeline for this preview.");
            else
            {
                var serialized = new SerializedObject(pipeline);
                var index = serialized.FindProperty("m_DefaultRendererIndex").intValue;
                var data = serialized.FindProperty("m_RendererDataList").GetArrayElementAtIndex(index).objectReferenceValue as ScriptableRendererData;
                if (!data || !data.rendererFeatures.Any(f => f && f.isActive && f.GetType().FullName == "Gsplat.GsplatURPFeature"))
                    messages.Add("Add the active Gsplat URP feature to the current default renderer.");
                var graph = GraphicsSettings.GetRenderPipelineSettings<RenderGraphSettings>();
                if (graph == null || graph.enableRenderCompatibilityMode) messages.Add("Disable URP Render Graph Compatibility Mode.");
            }
            if (SystemInfo.graphicsDeviceType != GraphicsDeviceType.Direct3D12 && SystemInfo.graphicsDeviceType != GraphicsDeviceType.Vulkan) messages.Add("Restart the Editor with Direct3D12 or Vulkan for this renderer.");
            var shader = AssetDatabase.LoadAssetAtPath<ComputeShader>("Packages/wu.yize.gsplat/Runtime/Shaders/Gsplat.compute");
            if (!shader) messages.Add("Install the pinned gsplat-unity package.");
            else foreach (string kernel in new[] { "InitPayload", "InitDeviceRadixSort", "Upsweep", "Scan", "Downsweep" })
                if (!shader.HasKernel(kernel) || !shader.IsSupported(shader.FindKernel(kernel))) messages.Add("Unsupported sorting kernel: " + kernel);
            var renderers = Object.FindObjectsByType<GsplatRenderer>(FindObjectsSortMode.None);
            if (renderers.Length == 0) messages.Add("Import the source and add its preview renderer.");
            foreach (var renderer in renderers.Where(r => r.GsplatAsset != null))
            {
                var importer = AssetImporter.GetAtPath(AssetDatabase.GetAssetPath(renderer.GsplatAsset)) as GsplatImporter;
                if (importer && (importer.Compression != CompressionMode.Uncompressed || importer.SourceCoordinates != SourceCoordinates.RUB))
                    messages.Add(renderer.name + ": this baseline expects Uncompressed import with one RUB-to-Unity conversion.");
                if (renderer.GsplatAsset.PrunedSplatCount != 0) messages.Add(renderer.name + ": disable opacity pruning before reference review.");
                if (renderer.RenderBeforeUploadComplete) messages.Add(renderer.name + ": reference rendering requires complete upload.");
            }
            return messages;
        }
    }
}
