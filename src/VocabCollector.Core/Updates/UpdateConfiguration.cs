namespace VocabCollector.Core.Updates;

public static class UpdateConfiguration
{
    public static readonly Uri ManifestUri = new("https://101.133.134.128/tuci-updates/stable.json");
    public const string Platform = "win-x64";
    public const string AppExecutableName = "兔词.exe";
    public const string UpdaterExecutableName = "兔词更新器.exe";
    public const string PublicKeyPem = """
-----BEGIN PUBLIC KEY-----
MIIBojANBgkqhkiG9w0BAQEFAAOCAY8AMIIBigKCAYEA/M2yy9YO/dxKfkj3dYrl
mIHYiOYAC5waTUlDJXWD46uGW4m+DlKaTj4ouXEe2LJ+oyc1GyhlOpokDqkU1sgx
Vn6JVtEgBVNgDsSzoa8dJ9LNyiAafW7nsMVrG6McfxVSsDXrAK8UoYTTIc2uTNgn
wow3+axxFLWxjqmBZRxAHuS1PjpNajx9rhc9GiqwcDBE0Fy2rxU/qhOJgmoiR8In
44kgM5s+0eB2LMjPBrnMKAGOmsaJq37M+A4+KmwhdjZArKApIAaSPF601ehanqyQ
90eJO/EA+ufhnkVv7/DrhN6HsNVo0Dm1vJnzq8couCJF6094hNILld3IkmHb89kr
i0AFRmvZyWLAWgE+padL1ORXD+zi6F3JYUClvjPbB+TfGivcOdiS28dKBi9MbH0I
11hPvk9OfxWoVU0r8QlS3XB3YN2n9CmPWvWK5bGVm4cTnJ34oyM34TxFCchC1thv
p2Rl+FMPD35gUhcJ9+Ro/waO5Cg4MOPb5tuRPezSdbhNAgMBAAE=
-----END PUBLIC KEY-----
""";
}
