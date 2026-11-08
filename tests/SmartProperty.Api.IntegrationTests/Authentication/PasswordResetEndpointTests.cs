using System.Net;
using System.Net.Http.Json;
using SmartProperty.Api.Contracts;
using SmartProperty.Api.Contracts.Authentication;
using SmartProperty.Api.IntegrationTests.Infrastructure;
using Xunit;

namespace SmartProperty.Api.IntegrationTests.Authentication;

/// <summary>
/// The three password-reset screens through the real host and database: the email, the code on its own, then the new
/// password with the token the code step returns.
/// </summary>
[Collection(ApiPostgreSqlCollection.Name)]
public sealed class PasswordResetEndpointTests : IDisposable
{
    private const string Forgot = "/api/auth/forgot-password";
    private const string Verify = "/api/auth/verify-reset-code";
    private const string Reset = "/api/auth/reset-password";
    private const string NewPassword = "a-brand-new-password";

    private readonly SmartPropertyApiFactory _factory;
    private readonly AuthScenario _scenario;

    public PasswordResetEndpointTests(ApiPostgreSqlFixture fixture)
    {
        _factory = new SmartPropertyApiFactory(fixture);
        _scenario = new AuthScenario(fixture);
    }

    public void Dispose()
    {
        _factory.Dispose();
    }

    [Fact]
    public async Task TheThreeStepsChangeThePasswordAndOnlyTheNewOneSignsIn()
    {
        using var client = _factory.CreateClient();
        var user = await _scenario.RegisterActiveUserAsync(client);

        using var forgot = await client.PostAsJsonAsync(Forgot, new ForgotPasswordRequest(user.Email));
        var code = _factory.EmailSender.LatestCodeFor(user.Email);
        using var verify = await client.PostAsJsonAsync(Verify, new VerifyResetCodeRequest(user.Email, code));

        Assert.Equal(HttpStatusCode.OK, forgot.StatusCode);
        Assert.Equal(HttpStatusCode.OK, verify.StatusCode);
        var verified = (await verify.Content.ReadFromJsonAsync<VerifyResetCodeResponse>())!;
        Assert.Equal(43, verified.ResetToken.Length);
        Assert.True(verified.ExpiresAt > DateTimeOffset.UtcNow.AddMinutes(9));

        using var reset = await client.PostAsJsonAsync(
            Reset,
            new ResetPasswordRequest(user.Email, verified.ResetToken, NewPassword, NewPassword));

        Assert.Equal(HttpStatusCode.OK, reset.StatusCode);

        using var oldLogin = await client.PostAsJsonAsync("/api/auth/login", new LoginRequest(user.Email, AuthScenario.Password));
        using var newLogin = await client.PostAsJsonAsync("/api/auth/login", new LoginRequest(user.Email, NewPassword));
        Assert.Equal(HttpStatusCode.Unauthorized, oldLogin.StatusCode);
        Assert.Equal(HttpStatusCode.OK, newLogin.StatusCode);
    }

    [Fact]
    public async Task TheCodeIsSpentByTheVerifyStepAndTheTokenByTheResetStep()
    {
        using var client = _factory.CreateClient();
        var user = await _scenario.RegisterActiveUserAsync(client);
        var token = await VerifiedTokenAsync(client, user.Email);

        using var codeAgain = await client.PostAsJsonAsync(
            Verify,
            new VerifyResetCodeRequest(user.Email, _factory.EmailSender.LatestCodeFor(user.Email)));
        using var first = await client.PostAsJsonAsync(Reset, new ResetPasswordRequest(user.Email, token, NewPassword, NewPassword));
        using var second = await client.PostAsJsonAsync(Reset, new ResetPasswordRequest(user.Email, token, "another-new-password", "another-new-password"));

        Assert.Equal(HttpStatusCode.UnprocessableEntity, codeAgain.StatusCode);
        Assert.Equal("password_reset.invalid_code", (await codeAgain.Content.ReadFromJsonAsync<ApiErrorResponse>())!.Code);
        Assert.Equal(HttpStatusCode.OK, first.StatusCode);
        Assert.Equal(HttpStatusCode.UnprocessableEntity, second.StatusCode);
        Assert.Equal("password_reset.invalid_token", (await second.Content.ReadFromJsonAsync<ApiErrorResponse>())!.Code);
    }

    [Fact]
    public async Task AWrongOrUnknownCodeGetsOneAnswer()
    {
        using var client = _factory.CreateClient();
        var user = await _scenario.RegisterActiveUserAsync(client);
        (await client.PostAsJsonAsync(Forgot, new ForgotPasswordRequest(user.Email))).Dispose();

        using var wrong = await client.PostAsJsonAsync(Verify, new VerifyResetCodeRequest(user.Email, "000000"));
        using var unknown = await client.PostAsJsonAsync(Verify, new VerifyResetCodeRequest("nobody@example.com", "000000"));

        Assert.Equal(HttpStatusCode.UnprocessableEntity, wrong.StatusCode);
        Assert.Equal(HttpStatusCode.UnprocessableEntity, unknown.StatusCode);
        Assert.Equal("password_reset.invalid_code", (await wrong.Content.ReadFromJsonAsync<ApiErrorResponse>())!.Code);
        Assert.Equal("password_reset.invalid_code", (await unknown.Content.ReadFromJsonAsync<ApiErrorResponse>())!.Code);
    }

    [Fact]
    public async Task AMissingOrMalformedEmailIsAValidationError()
    {
        using var client = _factory.CreateClient();

        using var verify = await client.PostAsJsonAsync(Verify, new VerifyResetCodeRequest("not-an-email", "123456"));
        using var empty = await client.PostAsJsonAsync(Verify, new { });

        Assert.Equal(HttpStatusCode.UnprocessableEntity, verify.StatusCode);
        Assert.Equal(HttpStatusCode.UnprocessableEntity, empty.StatusCode);
    }

    [Fact]
    public async Task TheResetNeedsTheTokenAndNoCodeFromTheOldCombinedShapeIsAccepted()
    {
        using var client = _factory.CreateClient();
        var user = await _scenario.RegisterActiveUserAsync(client);
        (await client.PostAsJsonAsync(Forgot, new ForgotPasswordRequest(user.Email))).Dispose();
        var code = _factory.EmailSender.LatestCodeFor(user.Email);

        using var withCode = await client.PostAsJsonAsync(
            Reset,
            new { email = user.Email, code, newPassword = NewPassword, confirmPassword = NewPassword });
        using var noVerify = await client.PostAsJsonAsync(
            Reset,
            new ResetPasswordRequest(user.Email, "Z".PadRight(43, 'Z'), NewPassword, NewPassword));

        Assert.Equal(HttpStatusCode.UnprocessableEntity, withCode.StatusCode);
        Assert.Equal("password_reset.invalid_token", (await withCode.Content.ReadFromJsonAsync<ApiErrorResponse>())!.Code);
        Assert.Equal("password_reset.invalid_token", (await noVerify.Content.ReadFromJsonAsync<ApiErrorResponse>())!.Code);

        using var stillOld = await client.PostAsJsonAsync("/api/auth/login", new LoginRequest(user.Email, AuthScenario.Password));
        Assert.Equal(HttpStatusCode.OK, stillOld.StatusCode);
    }

    [Theory]
    [InlineData("a-brand-new-password", "a-different-confirmation")]
    [InlineData("short", "short")]
    [InlineData("", "")]
    public async Task AMismatchedOrTooShortPasswordIsRefusedAndTheTokenStaysUsable(string password, string confirm)
    {
        using var client = _factory.CreateClient();
        var user = await _scenario.RegisterActiveUserAsync(client);
        var token = await VerifiedTokenAsync(client, user.Email);

        using var refused = await client.PostAsJsonAsync(Reset, new ResetPasswordRequest(user.Email, token, password, confirm));
        using var accepted = await client.PostAsJsonAsync(Reset, new ResetPasswordRequest(user.Email, token, NewPassword, NewPassword));

        Assert.Equal(HttpStatusCode.UnprocessableEntity, refused.StatusCode);
        Assert.Equal("validation.failed", (await refused.Content.ReadFromJsonAsync<ApiErrorResponse>())!.Code);
        Assert.Equal(HttpStatusCode.OK, accepted.StatusCode);
    }

    [Fact]
    public async Task AnotherUsersTokenDoesNotWorkForThisEmail()
    {
        using var client = _factory.CreateClient();
        var first = await _scenario.RegisterActiveUserAsync(client);
        var second = await _scenario.RegisterActiveUserAsync(client);
        var firstToken = await VerifiedTokenAsync(client, first.Email);

        using var response = await client.PostAsJsonAsync(
            Reset,
            new ResetPasswordRequest(second.Email, firstToken, NewPassword, NewPassword));

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        Assert.Equal("password_reset.invalid_token", (await response.Content.ReadFromJsonAsync<ApiErrorResponse>())!.Code);
        using var secondLogin = await client.PostAsJsonAsync("/api/auth/login", new LoginRequest(second.Email, AuthScenario.Password));
        Assert.Equal(HttpStatusCode.OK, secondLogin.StatusCode);
    }

    [Fact]
    public async Task AResetSignsTheAccountOutEverywhere()
    {
        using var client = _factory.CreateClient();
        var signedIn = await _scenario.SignInAsync(client);
        var token = await VerifiedTokenAsync(client, signedIn.User.Email);

        (await client.PostAsJsonAsync(
            Reset,
            new ResetPasswordRequest(signedIn.User.Email, token, NewPassword, NewPassword))).Dispose();

        using var refresh = await AuthScenario.PostRefreshAsync(client, signedIn.Tokens.RefreshToken);
        Assert.Equal(HttpStatusCode.Unauthorized, refresh.StatusCode);
    }

    private async Task<string> VerifiedTokenAsync(HttpClient client, string email)
    {
        (await client.PostAsJsonAsync(Forgot, new ForgotPasswordRequest(email))).Dispose();
        using var verify = await client.PostAsJsonAsync(
            Verify,
            new VerifyResetCodeRequest(email, _factory.EmailSender.LatestCodeFor(email)));
        verify.EnsureSuccessStatusCode();

        return (await verify.Content.ReadFromJsonAsync<VerifyResetCodeResponse>())!.ResetToken;
    }
}
