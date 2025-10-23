using Microsoft.AspNetCore.Identity;

namespace OyunIndirCom.Models
{
    public class ApplicationUser : IdentityUser // IdentityUser EFC ile gelen default model. ApplicationUser ile IdentityUser'den kalıtım alarak IdentityUser ile hali hazırda gelen propertyleri de kullanabilir oluyoruz.
    {
        public string Nickname { get; set; }
        // Kendi extra property'miz. IdentityUser ile gelen tüm propertylerin (Id, Email, Phone Number) dışında kendi propertyimiz.
    }
}
