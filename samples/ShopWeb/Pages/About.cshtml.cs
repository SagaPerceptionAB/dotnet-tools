using System.ComponentModel.DataAnnotations;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace ShopWeb.Pages;

public class AboutModel : PageModel
{
    [BindProperty]
    public ContactInput Input { get; set; } = new();

    public bool Sent { get; private set; }

    public void OnPost()
    {
        if (ModelState.IsValid) Sent = true;
    }

    public class ContactInput
    {
        [Required(ErrorMessage = "Namn måste fyllas i.")]
        [StringLength(60, MinimumLength = 2, ErrorMessage = "Namnet ska vara mellan 2 och 60 tecken.")]
        [Display(Name = "Ditt namn")]
        public string Name { get; set; } = "";

        [Required(ErrorMessage = "E-post måste fyllas i.")]
        [EmailAddress(ErrorMessage = "E-postadressen är ogiltig.")]
        [Display(Name = "E-post")]
        public string Email { get; set; } = "";

        [Required(ErrorMessage = "Meddelande måste fyllas i.")]
        [MaxLength(500, ErrorMessage = "Meddelandet får vara högst 500 tecken.")]
        [Display(Name = "Meddelande")]
        public string Message { get; set; } = "";
    }
}
