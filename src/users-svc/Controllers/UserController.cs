using Application.DTO.UsersDTO;
using Domain.Enums;
using Domain.Interfaces.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using MongoDB.Bson;

namespace Controllers
{
    [ApiController]
    [Route("api/[controller]")]
    public class UsersController(IUserService service) : ControllerBase
    {
        [HttpGet]
        [Authorize(Roles = nameof(UserRole.Admin))]
        public async Task<IActionResult> Get()
        {
            return Ok(await service.GetAllAsync());
        }

        [HttpGet("{id:length(24)}")]
        public async Task<IActionResult> Get(ObjectId id)
        {
            var user = await service.GetByIdAsync(id);
            return user is null ? NotFound() : Ok(user);
        }

        [HttpPost]
        [Authorize(Roles = nameof(UserRole.Admin))]
        public async Task<IActionResult> Post(CreateUserDTO user)
        {
            var createdUser = await service.CreateAsync(user);
            return CreatedAtAction(nameof(Get), createdUser);
        }

        [HttpPost("admin")]
        [Authorize(Roles = nameof(UserRole.Admin))]
        public async Task<IActionResult> CreateAdmin(CreateUserAdminDTO user)
        {
            var createdUser = await service.CreateAsync(user);
            return CreatedAtAction(nameof(Get), createdUser);
        }

        [HttpPut("{id:length(24)}")]
        [Authorize(Roles = nameof(UserRole.Admin))]
        public async Task<IActionResult> Put(string id, UpdateUserDTO user)
        {
            var existing = await service.GetByIdAsync(ObjectId.Parse(id));
            if (existing is null) return NotFound();

            await service.UpdateAsync(ObjectId.Parse(id), user);
            return NoContent();
        }

        [HttpDelete("{id:length(24)}")]
        [Authorize(Roles = nameof(UserRole.Admin))]
        public async Task<IActionResult> Delete(string id)
        {
            var existing = await service.GetByIdAsync(ObjectId.Parse(id));
            if (existing is null) return NotFound();

            await service.DeleteAsync(ObjectId.Parse(id));
            return NoContent();
        }
    }
}
