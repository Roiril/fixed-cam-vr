#nullable enable

using UnityEngine;

namespace FixedCamVr.Streaming
{
    /// <summary>破砕開始時に借りる左右眼の実景と、撮影時のワールド座標から UV への射影。</summary>
    public readonly struct IntroFrozenFrameSource
    {
        public IntroFrozenFrameSource(
            Texture left,
            Texture right,
            Matrix4x4 leftWorldToUv,
            Matrix4x4 rightWorldToUv)
        {
            Left = left;
            Right = right;
            LeftWorldToUv = leftWorldToUv;
            RightWorldToUv = rightWorldToUv;
        }

        public Texture Left { get; }
        public Texture Right { get; }
        public Matrix4x4 LeftWorldToUv { get; }
        public Matrix4x4 RightWorldToUv { get; }
    }
}
