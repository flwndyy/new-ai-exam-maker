using System.Text.RegularExpressions;

namespace ExamMaker.Api.Authentication;

// Cognito configuration is supplied by the environment or Parameter Store.
public sealed class CognitoOptions
{
    public const string SectionName = "Cognito";
    /// <summary>AWS region containing the user pool, for example ap-southeast-1.</summary>
    public string Region { get; set; } = string.Empty;
    /// <summary>Cognito user pool identifier used to identify the token issuer.</summary>
    public string UserPoolId { get; set; } = string.Empty;
    /// <summary>Frontend app client identifier checked against access-token client_id.</summary>
    public string ClientId { get; set; } = string.Empty;

    /// <summary>Distinguish an absent configuration from a partially supplied configuration.</summary>
    public bool HasAnyValue => !string.IsNullOrEmpty(Region) || !string.IsNullOrEmpty(UserPoolId) || !string.IsNullOrEmpty(ClientId);
    /// <summary>Indicate whether the configuration is valid for real Cognito authentication.</summary>
    public bool IsConfigured => HasValidValues();
    /// <summary>Build the user pool issuer, which differs from the managed login domain.</summary>
    public string Issuer => $"https://cognito-idp.{Region}.amazonaws.com/{UserPoolId}";

    /// <summary>Validate all three settings before constructing a discovery address.</summary>
    public bool HasValidValues() =>
        !string.IsNullOrWhiteSpace(Region) && !string.IsNullOrWhiteSpace(UserPoolId) && !string.IsNullOrWhiteSpace(ClientId)
        && Regex.IsMatch(Region, @"\A[a-z]{2}(?:-[a-z]+)+-[0-9]+\z")
        && Regex.IsMatch(UserPoolId, @"\A" + Regex.Escape(Region) + @"_[a-zA-Z0-9]+\z")
        && Regex.IsMatch(ClientId, @"\A[a-zA-Z0-9]{1,128}\z");
}
