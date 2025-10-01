using Domain.Entities;
using Domain.Enums;

namespace Application.DTO.UsersDTO
{
    public class CreateUserAdminDTO : CreateUserDTO
    {
        public CreateUserAdminDTO()
        {

        }

        public override User ToEntity()
        {
            return new User
            {
                Name = Name,
                Email = Email,
                Password = Password,
                Role = UserRole.Admin
            };
        }
    }
}
