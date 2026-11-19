using System.Security.Claims;
using System.IdentityModel.Tokens.Jwt;
using ExamMaker.Api.Authorization;
using ExamMaker.Application.Authentication;
using ExamMaker.Infrastructure.Authentication;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Authorization;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;

namespace ExamMaker.Api.Authentication;

public static class AuthenticationServiceCollectionExtensions
{
    /// <summary>Đăng ký Cognito, user theo request và policy; không chứa khóa ký hay credential AWS.</summary>
    public static IServiceCollection AddExamMakerAuthentication(this IServiceCollection services, IConfiguration configuration)
    {
        services.AddOptions<CognitoOptions>().Bind(configuration.GetSection(CognitoOptions.SectionName))
            .Validate(x => !x.HasAnyValue || x.HasValidValues(),
                "Provide valid Cognito:Region, Cognito:UserPoolId and Cognito:ClientId together.")
            .ValidateOnStart();
        services.AddMemoryCache();
        // Scoped tạo hồ sơ riêng cho từng request, tránh lẫn quyền của hai người dùng.
        services.AddScoped<IUserDirectory, DatabaseUserDirectory>();
        services.AddScoped<CurrentUser>();
        services.AddScoped<ICurrentUser>(provider => provider.GetRequiredService<CurrentUser>());
        services.AddScoped<CognitoBearerEvents>();

        services.AddAuthentication(JwtBearerDefaults.AuthenticationScheme).AddJwtBearer();
        services.AddOptions<JwtBearerOptions>(JwtBearerDefaults.AuthenticationScheme)
            .Configure<IOptions<CognitoOptions>>((jwt, configured) => ConfigureBearer(jwt, configured.Value));

        var authenticated = new AuthorizationPolicyBuilder(JwtBearerDefaults.AuthenticationScheme)
            .RequireAuthenticatedUser().RequireClaim(CognitoBearerEvents.UserIdClaim).Build();
        services.AddAuthorization(options =>
        {
            // Bảo vệ cả endpoint quên gắn [Authorize]; chỉ endpoint AllowAnonymous được công khai.
            options.DefaultPolicy = authenticated;
            options.FallbackPolicy = authenticated;
            options.AddPolicy(AccessPolicies.Admin, policy => policy.Combine(authenticated).RequireRole("admin"));
            options.AddPolicy(AccessPolicies.ContentAuthor, policy => policy.Combine(authenticated).RequireRole("teacher", "head"));
            foreach (var permission in new[] { AccessPolicies.SameDepartment, AccessPolicies.Owner, AccessPolicies.Reviewer })
            {
                // Các policy này cần resource do controller tải từ DB, ngoài việc đăng nhập.
                options.AddPolicy(permission, policy => policy.Combine(authenticated).AddRequirements(new ContentAccessRequirement(permission)));
            }
        });
        services.AddScoped<IAuthorizationHandler, ContentAccessHandler>();
        return services;
    }

    /// <summary>Kiểm chữ ký RS256, issuer, hạn dùng và client_id của access token Cognito.</summary>
    private static void ConfigureBearer(JwtBearerOptions jwt, CognitoOptions cognito)
    {
        if (cognito.IsConfigured) jwt.Authority = cognito.Issuer;
        jwt.RequireHttpsMetadata = true;
        // Giữ nguyên tên sub/client_id để đối chiếu đúng claim Cognito phát hành.
        jwt.MapInboundClaims = false;
        // Không giữ token trong phiên xác thực và không trả chi tiết lỗi token cho client.
        jwt.SaveToken = false;
        jwt.IncludeErrorDetails = false;
        jwt.EventsType = typeof(CognitoBearerEvents);
        jwt.TokenValidationParameters = new TokenValidationParameters
        {
            ValidateIssuer = true,
            ValidIssuer = cognito.IsConfigured ? cognito.Issuer : null,
            ValidateIssuerSigningKey = true,
            RequireSignedTokens = true,
            // Chỉ nhận thuật toán Cognito sử dụng, tránh nhận token unsigned hoặc HMAC giả.
            ValidAlgorithms = [SecurityAlgorithms.RsaSha256],
            ValidateLifetime = true,
            RequireExpirationTime = true,
            // Cho phép sai lệch đồng hồ nhỏ giữa máy API và Cognito.
            ClockSkew = TimeSpan.FromSeconds(30),
            // Cognito access token dùng client_id, còn ID token dùng aud và bị loại ở TokenValidated.
            ValidateAudience = true,
            AudienceValidator = (_, token, _) => HasExpectedClient(token, cognito.ClientId)
        };
    }

    /// <summary>Hỗ trợ cả hai token handler của .NET và không chấp nhận client_id trùng lặp.</summary>
    private static bool HasExpectedClient(SecurityToken token, string clientId)
    {
        IEnumerable<Claim> claims = token switch
        {
            JsonWebToken json => json.Claims,
            JwtSecurityToken jwt => jwt.Claims,
            _ => []
        };
        var principal = new ClaimsPrincipal(new ClaimsIdentity(claims));
        return !string.IsNullOrWhiteSpace(clientId)
            && CognitoBearerEvents.SingleClaim(principal, "client_id") == clientId;
    }
}
