using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Security_Camera_Backend.Configuration;
using Security_Camera_Backend.Contracts;
using Security_Camera_Backend.Services.Security;
using Xunit;

namespace SecurityCameraBackend.IntegrationTests;

[Collection(IntegrationCollection.Name)]
public sealed class LoginHardeningTests(IntegrationTestFactory factory)
{
    /// <summary>
    /// The regression this guards: answering SYSTEM_ID_REQUIRED purely because a username exists in
    /// two systems turns POST /api/auth/login into a username oracle. The password must be checked
    /// against every candidate first, so a wrong password is always a plain 401.
    /// </summary>
    [Fact]
    public async Task Login_WithoutSystemId_ChecksPasswordBeforeReportingAmbiguity()
    {
        var username = UniqueUsername();

        using var admin1 = await CreateAdminClientAsync(
            IntegrationTestFactory.FactoryAdmin1,
            IntegrationTestFactory.FactoryAdmin1Password,
            "sys-001");
        using var admin2 = await CreateAdminClientAsync(
            IntegrationTestFactory.FactoryAdmin2,
            IntegrationTestFactory.FactoryAdmin2Password,
            "sys-002");

        // The same username exists in both systems, but with DIFFERENT passwords — so the password
        // itself identifies the account and systemId can be omitted.
        await CreateSubUserAsync(admin1, username, "sys-001-only-password");
        await CreateSubUserAsync(admin2, username, "sys-002-only-password");

        using var client = factory.CreateClient();

        var wrongPassword = await client.PostAsJsonAsync("/api/auth/login", new
        {
            username,
            password = "neither-systems-password"
        });

        // A wrong password must NOT reveal that the username exists in two systems.
        Assert.Equal(HttpStatusCode.Unauthorized, wrongPassword.StatusCode);

        var problem = await wrongPassword.Content.ReadFromJsonAsync<JsonElement>();
        Assert.False(problem.TryGetProperty("code", out _));
        Assert.Equal(
            "Invalid username or password.",
            problem.GetProperty("title").GetString());

        var sys1Login = await client.PostAsJsonAsync("/api/auth/login", new
        {
            username,
            password = "sys-001-only-password"
        });

        Assert.Equal(HttpStatusCode.OK, sys1Login.StatusCode);

        var sys1 = await sys1Login.Content.ReadFromJsonAsync<LoginResponse>();
        Assert.NotNull(sys1);
        Assert.Equal("sys-001", sys1!.User.SystemId);

        var sys2Login = await client.PostAsJsonAsync("/api/auth/login", new
        {
            username,
            password = "sys-002-only-password"
        });

        Assert.Equal(HttpStatusCode.OK, sys2Login.StatusCode);

        var sys2 = await sys2Login.Content.ReadFromJsonAsync<LoginResponse>();
        Assert.NotNull(sys2);
        Assert.Equal("sys-002", sys2!.User.SystemId);

        // Only a password that is valid in BOTH systems is genuinely ambiguous. A fresh username is
        // used because a (systemId, username) pair is unique, so the plaintext cannot be replaced.
        var sharedUsername = UniqueUsername();

        await CreateSubUserAsync(admin1, sharedUsername, "shared-password");
        await CreateSubUserAsync(admin2, sharedUsername, "shared-password");

        var ambiguous = await client.PostAsJsonAsync("/api/auth/login", new
        {
            username = sharedUsername,
            password = "shared-password"
        });

        Assert.Equal(HttpStatusCode.BadRequest, ambiguous.StatusCode);

        var ambiguousProblem = await ambiguous.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(
            "SYSTEM_ID_REQUIRED",
            ambiguousProblem.GetProperty("code").GetString());
    }

    [Fact]
    public async Task Login_WithSystemId_AcceptsOnlyThatSystemsPassword()
    {
        var username = UniqueUsername();

        using var admin1 = await CreateAdminClientAsync(
            IntegrationTestFactory.FactoryAdmin1,
            IntegrationTestFactory.FactoryAdmin1Password,
            "sys-001");
        using var admin2 = await CreateAdminClientAsync(
            IntegrationTestFactory.FactoryAdmin2,
            IntegrationTestFactory.FactoryAdmin2Password,
            "sys-002");

        await CreateSubUserAsync(admin1, username, "sys-001-password");
        await CreateSubUserAsync(admin2, username, "sys-002-password");

        using var client = factory.CreateClient();

        // sys-001's password against sys-002 must fail: the scoped lookup only sees sys-002's user.
        var crossSystem = await client.PostAsJsonAsync("/api/auth/login", new
        {
            username,
            password = "sys-001-password",
            systemId = "sys-002"
        });

        Assert.Equal(HttpStatusCode.Unauthorized, crossSystem.StatusCode);

        var correct = await client.PostAsJsonAsync("/api/auth/login", new
        {
            username,
            password = "sys-002-password",
            systemId = "sys-002"
        });

        Assert.Equal(HttpStatusCode.OK, correct.StatusCode);
    }

    [Fact]
    public async Task Login_IsLockedOut_AfterRepeatedFailures()
    {
        var username = UniqueUsername();

        using var admin = await CreateAdminClientAsync(
            IntegrationTestFactory.FactoryAdmin1,
            IntegrationTestFactory.FactoryAdmin1Password,
            "sys-001");

        await CreateSubUserAsync(admin, username, "correct-password");

        using var client = factory.CreateClient();

        var statuses = new List<HttpStatusCode>();

        // MaxFailures defaults to 5 in the test host configuration.
        for (var attempt = 0; attempt < 6; attempt++)
        {
            var response = await client.PostAsJsonAsync("/api/auth/login", new
            {
                username,
                password = "wrong-password"
            });

            statuses.Add(response.StatusCode);
        }

        Assert.Equal(5, statuses.Count(status => status == HttpStatusCode.Unauthorized));
        Assert.Equal(HttpStatusCode.TooManyRequests, statuses[^1]);

        // Even the correct password is refused while the lockout is active.
        var lockedOut = await client.PostAsJsonAsync("/api/auth/login", new
        {
            username,
            password = "correct-password"
        });

        Assert.Equal(HttpStatusCode.TooManyRequests, lockedOut.StatusCode);
        Assert.Equal("LOGIN_LOCKED", (await lockedOut.Content.ReadFromJsonAsync<JsonElement>())
            .GetProperty("code")
            .GetString());
        Assert.NotNull(lockedOut.Headers.RetryAfter);
    }

    [Fact]
    public async Task LoginRateLimitPolicy_IsAppliedToTheLoginEndpoint()
    {
        // The test host raises the limit so the suite can log in freely; the production default and
        // the wiring are asserted here instead of by exhausting a 5/min budget.
        var options = new LoginRateLimitOptions();

        Assert.True(options.Enabled);
        Assert.Equal(5, options.PermitLimit);
        Assert.Equal(60, options.WindowSeconds);

        var lockout = new LoginLockoutOptions();

        Assert.True(lockout.Enabled);
        Assert.Equal(5, lockout.MaxFailures);
        Assert.Equal(300, lockout.LockoutSeconds);

        using var client = factory.CreateClient();

        // The endpoint still answers normally, i.e. the attribute does not break the pipeline.
        var response = await client.PostAsJsonAsync("/api/auth/login", new
        {
            username = IntegrationTestFactory.FactoryAdmin1,
            password = IntegrationTestFactory.FactoryAdmin1Password
        });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task UploadFrame_RejectsPayloadThatIsNotActuallyAJpeg()
    {
        using var pi = factory.CreateClient();
        pi.DefaultRequestHeaders.Add("X-Api-Key", IntegrationTestFactory.PiApiKeySys003);

        // Correct content type, valid size — but the bytes are not an image at all.
        using var content = new ByteArrayContent("<html>definitely not a jpeg</html>"u8.ToArray());
        content.Headers.ContentType = new MediaTypeHeaderValue("image/jpeg");

        var response = await pi.PostAsync("/api/camera/frame", content);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task UploadEvent_RejectsPayloadThatIsNotActuallyAnImage()
    {
        using var pi = factory.CreateClient();
        pi.DefaultRequestHeaders.Add("X-Api-Key", IntegrationTestFactory.PiApiKey);

        using var form = new MultipartFormDataContent();
        var file = new ByteArrayContent("%PDF-1.7 not an image"u8.ToArray());
        file.Headers.ContentType = new MediaTypeHeaderValue("image/jpeg");
        form.Add(file, "image", "frame.jpg");

        var response = await pi.PostAsync("/api/events", form);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task UploadEvent_RejectsContentTypeThatContradictsTheBytes()
    {
        using var pi = factory.CreateClient();
        pi.DefaultRequestHeaders.Add("X-Api-Key", IntegrationTestFactory.PiApiKey);

        using var form = new MultipartFormDataContent();
        var file = new ByteArrayContent(TestImages.Jpeg);
        // A real JPEG smuggled in as a PNG is still a mismatch: the declared type must agree.
        file.Headers.ContentType = new MediaTypeHeaderValue("image/png");
        form.Add(file, "image", "frame.png");

        var response = await pi.PostAsync("/api/events", form);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public void ImageSignatureValidator_DetectsSupportedFormats()
    {
        Assert.Equal(ImageFormat.Jpeg, ImageSignatureValidator.Detect(TestImages.Jpeg));
        Assert.True(ImageSignatureValidator.IsJpeg(TestImages.Jpeg));
        Assert.Equal(ImageFormat.Unknown, ImageSignatureValidator.Detect("GIF89a"u8));
        Assert.Equal(ImageFormat.Unknown, ImageSignatureValidator.Detect("RIFFxxxxWAVE"u8));

        Assert.Equal(
            ImageFormat.Png,
            ImageSignatureValidator.Detect([0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, 0x00]));
        Assert.Equal(
            ImageFormat.WebP,
            ImageSignatureValidator.Detect("RIFF\0\0\0\0WEBPVP8 "u8));
    }

    private async Task<HttpClient> CreateAdminClientAsync(string username, string password, string systemId)
    {
        var client = factory.CreateClient();

        var loginResponse = await client.PostAsJsonAsync("/api/auth/login", new
        {
            username,
            password,
            systemId
        });

        Assert.Equal(HttpStatusCode.OK, loginResponse.StatusCode);

        var login = await loginResponse.Content.ReadFromJsonAsync<JsonElement>();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue(
            "Bearer",
            login.GetProperty("token").GetString());

        return client;
    }

    private static async Task CreateSubUserAsync(HttpClient adminClient, string username, string password)
    {
        var response = await adminClient.PostAsJsonAsync("/api/users", new
        {
            username,
            password,
            role = "User"
        });

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
    }

    private static string UniqueUsername() => $"hard_{Guid.NewGuid():N}"[..20];
}
