using Application.DTO.UsersDTO;

namespace Application.DTO.AuthenticationDTO
{
    public class AuthenticationTokenDTO
    {
        public string Token { get; set; } = string.Empty;
        public DateTime? ExpiresOn { get; set; }
        public ProjectUserDTO? UserInfo { get; set; }
    }
}
