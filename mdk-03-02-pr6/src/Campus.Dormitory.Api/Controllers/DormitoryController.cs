using Campus.Dormitory.Api.Data;
using Campus.Dormitory.Api.Models;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace Campus.Dormitory.Api.Controllers;

/// <summary>
/// Модуль «Общежитие и заселение». Атрибут [ApiController] включает автоматическую проверку модели:
/// при нарушении [Required] или [Range] клиент получает 400 Bad Request ещё до вызова метода.
/// </summary>
[ApiController]
[Route("api/v1/dormitory/checkin")]
public class DormitoryController(DormitoryDbContext db) : ControllerBase
{
    // POST /api/v1/dormitory/checkin — зарегистрировать заселение
    [HttpPost]
    public async Task<ActionResult<DormitoryCheckIn>> CheckIn(DormitoryCheckIn checkIn)
    {
        checkIn.Id = 0;                       // идентификатор назначает база данных
        db.CheckIns.Add(checkIn);
        await db.SaveChangesAsync();
        return Ok(checkIn);
    }

    // GET /api/v1/dormitory/checkin — список заселений
    [HttpGet]
    public async Task<ActionResult<List<DormitoryCheckIn>>> GetAll() =>
        Ok(await db.CheckIns.AsNoTracking().OrderBy(c => c.Id).ToListAsync());
}
