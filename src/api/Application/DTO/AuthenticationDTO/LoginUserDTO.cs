using Application.DTO.Bases.Interfaces;
using Domain.Models.Validation;

namespace Application.DTO.AuthenticationDTO
{
    public class LoginUserDTO : IValidator
    {
        public string Email { get; set; } = string.Empty;

        public string Password { get; set; } = string.Empty;

        public ValidationResultModel Validate()
        {
            var response = new ValidationResultModel();

            if (string.IsNullOrWhiteSpace(Email))
                response.AddError("Email não preenchido");
            else if (!IsValidEmail(Email))
                response.AddError("Formato de email inválido");

            if (string.IsNullOrWhiteSpace(Password))
                response.AddError("Senha não preenchida");

            return response;
        }

        private bool IsValidEmail(string email)
        {
            try
            {
                var addr = new System.Net.Mail.MailAddress(email);
                return addr.Address == email;
            }
            catch
            {
                return false;
            }
        }
    }
}
