using Application.DTO.Bases;
using Domain.Entities;
using MongoDB.Driver;

namespace Application.DTO.UsersDTO
{
    public class UpdateUserDTO : BaseUpdateDTO<User>
    {
        public string? Name { get; set; }
        public string? Email { get; set; }
        public string? Password { get; set; }

        public override UpdateDefinition<User> GetUpdateDefinition()
        {
            var update = Builders<User>.Update;
            var updates = new List<UpdateDefinition<User>>();

            if (!string.IsNullOrEmpty(Name))
                updates.Add(update.Set(x => x.Name, Name));
            if (!string.IsNullOrEmpty(Email))
                updates.Add(update.Set(x => x.Email, Email));
            if (!string.IsNullOrEmpty(Password))
                updates.Add(update.Set(x => x.Password, Password));

            return update.Combine(updates);
        }
    }
}
