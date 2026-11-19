using System.Text.RegularExpressions;

namespace ExamMaker.Api.Authentication;

// Chứa cấu hình Cognito; giá trị thật được nạp từ môi trường/Parameter Store.
public sealed class CognitoOptions
{
    public const string SectionName = "Cognito";
    /// <summary>Vùng AWS chứa kho tài khoản, ví dụ ap-southeast-1.</summary>
    public string Region { get; set; } = string.Empty;
    /// <summary>ID kho tài khoản Cognito của dự án; dùng xác định bên cấp token.</summary>
    public string UserPoolId { get; set; } = string.Empty;
    /// <summary>ID ứng dụng frontend; API đối chiếu với client_id trong access token.</summary>
    public string ClientId { get; set; } = string.Empty;

    /// <summary>Phân biệt chưa cấu hình gì với nhập thiếu một phần để báo lỗi startup đúng.</summary>
    public bool HasAnyValue => !string.IsNullOrEmpty(Region) || !string.IsNullOrEmpty(UserPoolId) || !string.IsNullOrEmpty(ClientId);
    /// <summary>Cho biết đã đủ cấu hình hợp lệ để xác thực token bằng Cognito thật.</summary>
    public bool IsConfigured => HasValidValues();
    /// <summary>Tạo issuer/discovery của User Pool, không phải domain giao diện đăng nhập.</summary>
    public string Issuer => $"https://cognito-idp.{Region}.amazonaws.com{(Region.StartsWith("cn-", StringComparison.Ordinal) ? ".cn" : "")}/{UserPoolId}";

    /// <summary>Kiểm tra đủ ba giá trị và định dạng an toàn trước khi tạo địa chỉ discovery.</summary>
    public bool HasValidValues() =>
        !string.IsNullOrWhiteSpace(Region) && !string.IsNullOrWhiteSpace(UserPoolId) && !string.IsNullOrWhiteSpace(ClientId)
        && Regex.IsMatch(Region, @"\A[a-z]{2}(?:-[a-z]+)+-[0-9]+\z")
        && Regex.IsMatch(UserPoolId, @"\A" + Regex.Escape(Region) + @"_[a-zA-Z0-9]+\z")
        && Regex.IsMatch(ClientId, @"\A[a-zA-Z0-9]{1,128}\z");
}
