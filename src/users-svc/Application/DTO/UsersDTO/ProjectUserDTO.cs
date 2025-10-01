using Application.DTO.Bases;
using Domain.Entities;
using MongoDB.Bson;
using System.Linq.Expressions;

namespace Application.DTO.UsersDTO
{
    public class ProjectUserDTO : BaseProjectDTO<User, ProjectUserDTO>
    {
        public ObjectId _id { get; set; }
        public string Name { get; set; }
        public string Email { get; set; }

        public ProjectUserDTO(ObjectId id, string name, string email)
        {
            _id = id;
            Name = name;
            Email = email;
        }

        public ProjectUserDTO(User user)
        {
            _id = user._id;
            Name = user.Name;
            Email = user.Email;
        }

        public ProjectUserDTO()
        {

        }

        public override Expression<Func<User, ProjectUserDTO>> ProjectExpression()
        {
            return x => new ProjectUserDTO
            {
                _id = x._id,
                Name = x.Name,
                Email = x.Email
            };
        }
    }
}