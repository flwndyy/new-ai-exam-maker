// All user-facing Vietnamese strings live here, never inline in components.

export const vi = {
  app: {
    name: "AI Exam Maker",
    tagline: "Soạn đề thi bằng AI cho giáo viên THPT",
  },
  nav: {
    home: "Trang chủ",
    exams: "Đề thi",
  },
  home: {
    title: "Chào mừng đến với AI Exam Maker",
    description: "Sinh câu hỏi bằng AI, quản lý ngân hàng câu hỏi và ghép đề thi.",
  },
  examView: {
    title: "Chi tiết đề thi",
    missingId: "Thiếu mã đề thi trên đường dẫn.",
    currentId: "Mã đề thi hiện tại",
  },
  common: {
    loading: "Đang tải…",
  },
  auth: {
    redirecting: "Đang chuyển tới trang đăng nhập…",
    completing: "Đang hoàn tất đăng nhập…",
    callbackFailed: "Đăng nhập không thành công. Vui lòng thử lại.",
    retry: "Thử lại",
    deniedTitle: "Tài khoản chưa được cấp quyền hoặc đã bị khóa",
    deniedBody: "Vui lòng liên hệ quản trị viên của trường để được hỗ trợ.",
    misconfigured: "Hệ thống chưa được cấu hình đăng nhập. Vui lòng liên hệ quản trị viên.",
    networkError: "Không kết nối được máy chủ. Vui lòng thử lại.",
    logout: "Đăng xuất",
    roles: { teacher: "Giáo viên", head: "Tổ trưởng", admin: "Quản trị" },
  },
} as const;

export type Messages = typeof vi;
