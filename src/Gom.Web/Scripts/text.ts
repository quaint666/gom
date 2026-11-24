import type { Failure } from './api.js';

/** The sentence for a failure, in Vietnamese. The sign-in sentence never says whether the ID exists. */
export function errorText(failure: Failure): string {
  switch (failure.error) {
    case 'invalid_credentials': return 'Gom ID hoặc mật khẩu không đúng. Hãy thử lại.';
    case 'too_many_attempts':
      return failure.retryAfter === undefined
        ? 'Bạn thử quá nhiều lần. Hãy đợi một lúc rồi thử lại.'
        : `Bạn thử quá nhiều lần. Hãy đợi ${failure.retryAfter} giây rồi thử lại.`;
    case 'id_taken': return 'Gom ID này đã có người dùng. Hãy chọn ID khác.';
    case 'invalid_gom_id': return 'Gom ID chỉ gồm chữ thường, số và dấu gạch dưới, từ 3 đến 20 ký tự, và không được là từ dành riêng.';
    case 'password_too_short': return 'Mật khẩu cần ít nhất 10 ký tự.';
    case 'password_too_long': return 'Mật khẩu dài tối đa 128 ký tự.';
    case 'password_too_common': return 'Mật khẩu này quá phổ biến. Hãy chọn mật khẩu khác.';
    case 'invalid_display_name': return 'Tên hiển thị tối đa 60 ký tự và không được có ký tự điều khiển.';
    case 'wrong_password': return 'Mật khẩu hiện tại không đúng.';
    case 'cannot_disable_owner': return 'Không thể khóa tài khoản của chủ Gom.';
    case 'user_not_found': return 'Không tìm thấy ai có Gom ID này. Hãy kiểm tra lại.';
    case 'cannot_add_self': return 'Đây là Gom ID của chính bạn.';
    case 'already_friends': return 'Hai bạn đã là bạn bè.';
    case 'you_blocked_them': return 'Bạn đã chặn người này. Hãy bỏ chặn trước nếu muốn kết bạn.';
    case 'not_friends': return 'Chỉ nhắn tin được với bạn bè.';
    case 'cannot_chat_self': return 'Bạn không thể nhắn tin cho chính mình.';
    case 'empty_message': return 'Hãy nhập nội dung tin nhắn.';
    case 'message_too_long': return 'Tin nhắn dài tối đa 4.000 ký tự.';
    case 'invalid_message': return 'Tin nhắn có ký tự không hợp lệ.';
    case 'too_many_messages': return 'Bạn gửi quá nhanh. Hãy đợi một chút rồi gửi lại.';
    case 'client_id_reused': case 'invalid_client_id': case 'invalid_seq': return 'Không gửi được tin nhắn này. Hãy thử lại.';
    case 'invalid_title': return 'Tên nhóm cần từ 1 đến 60 ký tự và không được có ký tự điều khiển.';
    case 'no_members': return 'Hãy chọn ít nhất một người bạn để lập nhóm.';
    case 'group_too_big': case 'group_full': return 'Nhóm đã đủ số thành viên tối đa.';
    case 'already_member': return 'Người này đã ở trong nhóm.';
    case 'not_a_group': return 'Đây không phải là một nhóm.';
    case 'not_allowed': return 'Bạn không có quyền làm việc này trong nhóm.';
    case 'leader_must_hand_over': return 'Trưởng nhóm cần chuyển quyền cho người khác trước khi rời nhóm.';
    case 'invalid_role': case 'invalid_member': return 'Không thể thực hiện với thành viên này.';
    case 'invalid_reply': return 'Không trả lời được tin nhắn này vì nó đã bị thu hồi hoặc không còn nữa.';
    case 'invalid_query': return 'Hãy nhập từ cần tìm (tối đa 100 ký tự).';
    case 'pin_limit': return 'Chỉ ghim được tối đa 3 tin nhắn. Hãy bỏ ghim một tin trước.';
    case 'invalid_reaction': return 'Không dùng được biểu tượng cảm xúc này.';
    case 'use_leave': return 'Muốn ra khỏi nhóm thì chọn "Rời nhóm".';
    case 'not_your_message': return 'Chỉ thu hồi được tin nhắn của chính bạn.';
    case 'missing_fields': return 'Hãy điền đầy đủ các ô.';
    case 'network': return 'Không kết nối được. Kiểm tra mạng rồi thử lại.';
    case 'server': return 'Gom đang gặp sự cố. Hãy thử lại sau ít phút.';
    default: return 'Có lỗi xảy ra. Hãy thử lại.';
  }
}
