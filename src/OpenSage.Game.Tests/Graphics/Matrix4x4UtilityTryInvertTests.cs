// R15 FIX-1 guard tests for Matrix4x4Utility.TryInvert.
//
// The AotR lithlad headed gate died in ModelMesh.BuildRenderListWithWorldMatrix: a CameraOriented
// mesh whose bone world matrix is singular (a collapsed / zero-scale bone, which mod W3Ds do ship
// and animations do produce transiently) has no inverse, and the throwing Matrix4x4Utility.Invert
// took the whole render pass down mid-frame. TryInvert is the non-throwing form the render path
// now uses so it can degrade to the un-oriented world matrix instead (STANDING RULE: one bad
// asset never aborts the frame). The throwing overload is deliberately left as-is for callers
// whose matrices are engine-constructed and must be invertible.

using System;
using System.Numerics;
using OpenSage.Mathematics;
using Xunit;

namespace OpenSage.Tests.Graphics;

public class Matrix4x4UtilityTryInvertTests
{
    [Fact]
    public void TryInvert_InvertibleMatrix_ReturnsTrueAndTheActualInverse()
    {
        var m = Matrix4x4.CreateRotationZ(0.75f) * Matrix4x4.CreateTranslation(3, -4, 5);

        Assert.True(Matrix4x4Utility.TryInvert(m, out var inverse));

        var identity = m * inverse;
        Assert.Equal(1.0f, identity.M11, 4);
        Assert.Equal(1.0f, identity.M22, 4);
        Assert.Equal(1.0f, identity.M33, 4);
        Assert.Equal(1.0f, identity.M44, 4);
        Assert.Equal(0.0f, identity.M41, 4);
        Assert.Equal(0.0f, identity.M42, 4);
        Assert.Equal(0.0f, identity.M43, 4);
    }

    [Fact]
    public void TryInvert_ZeroScaleBoneMatrix_ReturnsFalseInsteadOfThrowing()
    {
        // The lithlad case: a bone scaled to nothing on one axis, then translated.
        var collapsed = Matrix4x4.CreateScale(1.0f, 0.0f, 1.0f) * Matrix4x4.CreateTranslation(10, 20, 30);

        Assert.False(Matrix4x4Utility.TryInvert(collapsed, out _));
    }

    [Fact]
    public void TryInvert_AllZeroMatrix_ReturnsFalse()
    {
        Assert.False(Matrix4x4Utility.TryInvert(default, out _));
    }

    [Fact]
    public void Invert_StillThrowsOnASingularMatrix()
    {
        // Contract check: TryInvert is an addition, not a silent relaxation of the old overload.
        var collapsed = Matrix4x4.CreateScale(0.0f);

        Assert.Throws<InvalidOperationException>(() => Matrix4x4Utility.Invert(collapsed));
    }
}
