#nullable enable
using System;
using UnityEngine;

namespace FixedCamVr.Streaming
{
    /// <summary>Resourcesが本番passを直接保持し、診断keywordのstripに依存しない。</summary>
    public sealed class CrtSignalResources : ScriptableObject
    {
        [SerializeField] private Shader? composite;
        [SerializeField] private Shader? sourcePsf;
        [SerializeField] private Shader? signalFilter;

        public Shader Composite => Require(composite);
        public Shader SourcePsf => Require(sourcePsf);
        public Shader SignalFilter => Require(signalFilter);

        public static CrtSignalResources Load()
            => Resources.Load<CrtSignalResources>("CrtSignalRuntime")
               ?? throw new InvalidOperationException("CRT runtime shader resources are missing");

        private static Shader Require(Shader? shader)
            => shader != null && shader.isSupported ? shader
                : throw new InvalidOperationException("CRT runtime shader is missing or unsupported");
    }
}
