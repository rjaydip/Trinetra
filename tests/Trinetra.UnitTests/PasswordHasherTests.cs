using Shouldly;
using Trinetra.Federation.Storage.Security;

namespace Trinetra.UnitTests;

public sealed class PasswordHasherTests
{
    [Fact]
    public void Decoy_IsCostMatchedToTheCurrentWorkFactor()
    {
        // The whole point of computing it rather than hardcoding a constant: it tracks
        // DefaultIterations, so raising the cost does not silently reopen the 4-H4 timing gap.
        PasswordHasher.Decoy.Iterations.ShouldBe(PasswordHasher.DefaultIterations);
        PasswordHasher.Decoy.Algorithm.ShouldBe(PasswordHasher.AlgorithmName);
    }

    [Fact]
    public void Decoy_NeverVerifies()
    {
        PasswordHasher.Verify("", PasswordHasher.Decoy).ShouldBeFalse();
        PasswordHasher.Verify("password", PasswordHasher.Decoy).ShouldBeFalse();
        PasswordHasher.Verify("correct horse battery staple", PasswordHasher.Decoy).ShouldBeFalse();
    }

    [Fact]
    public void Decoy_IsStableAcrossReads()
    {
        // A fresh hash each read would make the verify cost vary and defeat the purpose.
        PasswordHasher.Decoy.Hash.ShouldBe(PasswordHasher.Decoy.Hash);
        PasswordHasher.Decoy.Salt.ShouldBe(PasswordHasher.Decoy.Salt);
    }
}
