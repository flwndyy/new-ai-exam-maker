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
} as const;

export type Messages = typeof vi;
