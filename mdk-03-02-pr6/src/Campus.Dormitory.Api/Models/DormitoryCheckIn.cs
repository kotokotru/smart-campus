using System.ComponentModel.DataAnnotations;

namespace Campus.Dormitory.Api.Models;

/// <summary>Заселение студента в общежитие кампуса (вариант 10).</summary>
public class DormitoryCheckIn
{
    public int Id { get; set; }

    [Required] public string? ResidentPassport { get; set; }

    [Range(1, 9)] public int BuildingNumber { get; set; }

    public int RoomNumber { get; set; }
}
