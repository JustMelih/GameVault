using System.ComponentModel.DataAnnotations; // Properylere [Key], [Required] gibi özellikleri eklememizi sağlar.

namespace OyunIndirCom.Models
{
    public class VideoGames
    {
        [Key] // Primary Key
        public int Id { get; set; } // Otomatik artma

        [Required] // Null veya boş olamaz.
        [StringLength(100)]
        public string Title { get; set; } = null!; // "!null" 'un amacı sadece compiler'ın bize uyarı vermemesini sağlamak amacıyla kullanılır.

        public string Description { get; set; } = null!;

        [Display(Name = "Image URL")] // Formlarda ki etiket yazısını "Image URL" ile değiştirir.
        [DataType(DataType.ImageUrl)] // Form ve validation'lara, bunun geçerli bir URL olduğunu ve bir resimi gösterdiğini söyler.
        public string ImageUrl { get; set; } = null!;

        public string GameType { get; set; } = null!;

        public int Downloads { get; set; }

        public float GameSize { get; set; }

        public DateTime ReleaseDate { get; set; }

        public int Rating { get; set; }

        public int EditorsChoice { get; set; }

    }
}