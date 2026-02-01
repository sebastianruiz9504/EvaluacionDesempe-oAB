using Microsoft.AspNetCore.Mvc;

namespace EvaluacionDesempenoAB.Controllers
{
    public class HomeController : Controller
    {
        public IActionResult Index()
        {
            return View();
        }
    }
}
