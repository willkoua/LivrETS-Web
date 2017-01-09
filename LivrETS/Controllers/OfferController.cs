﻿using Google.Apis.Books.v1.Data;
using LivrETS.Models;
using LivrETS.Repositories;
using LivrETS.Service.IO;
using LivrETS.ViewModels;
using Microsoft.AspNet.Identity;
using System;
using System.Collections.Generic;
using System.Configuration;
using System.Linq;
using System.Net;
using System.Net.Mail;
using System.Threading;
using System.Web;
using System.Web.Mvc;

namespace LivrETS.Controllers
{
    public class OfferController : Controller
    {
        private const string UPLOADS_PATH = "~/Content/Uploads";

        private LivrETSRepository _repository;
        public LivrETSRepository Repository
        {
            get
            {
                if (_repository == null)
                    _repository = new LivrETSRepository();

                return _repository;
            }
            private set
            {
                _repository = value;
            }
        }

        public OfferController(LivrETSRepository livretsRepository)
        {
            Repository = livretsRepository;
        }

        public OfferController() { }

        // GET: Offer
        public ActionResult Index()
        {
            return View();
        }

        // GET: Offer/Details/5
        public ActionResult Details(string id)
        {
            if (id == null)
                throw new HttpException(404, "Page not Found");

            Offer offer = Repository.GetOfferBy(id);
            ViewBag.user = Repository.GetOfferByUser(offer);

            return View(offer);
        }

        // GET: Offer/Create
        public ActionResult Create()
        {
            var model = new ArticleViewModel();

            Session["images"] = null;
            model.Courses = Repository.GetAllCourses().ToList();
            ThreadPool.QueueUserWorkItem(state =>
            {
                var arguments = state as Tuple<string, string>;
                FileSystemFacade.CleanTempFolder(uploadsPath: arguments.Item1, userId: arguments.Item2);
            }, state: new Tuple<string, string>(Server.MapPath(UPLOADS_PATH), User.Identity.GetUserId()));

            return View(model);
        }

        // POST: Offer/Create
        [HttpPost]
        public ActionResult Create(ArticleViewModel model)
        {
            var course = Repository.GetCourseByAcronym(model.Acronym);

            if (model.Type.Equals(Article.BOOK_CODE))
            {
                var result = BookApi.Search(model.ISBN, model.Title);

                // Validating the model
                if (!result)
                {
                    ModelState.AddModelError(nameof(ArticleViewModel.ISBN),
                        "Votre ISBN ne correspond pas au Titre de l'article ");
                }
            }
            
            if (model.Type != Article.CALCULATOR_CODE && string.IsNullOrEmpty(model.ISBN))
            {
                ModelState.AddModelError(nameof(ArticleViewModel.ISBN), 
                    "Le type choisi requiert un ISBN ou un code.");
            }

            if (course == null && model.Type != Article.CALCULATOR_CODE)
            {
                ModelState.AddModelError(nameof(ArticleViewModel.Acronym), 
                    "Le type choisi requiert un cours de la liste.");
            }

            // Proceeding to add the new offer.
            if (ModelState.IsValid)
            {
                Article newArticle = null;
                var uploadsPath = Server.MapPath(UPLOADS_PATH);

                switch (model.Type)
                {
                    case Article.BOOK_CODE:
                        newArticle = new Book()
                        {
                            Course = course,
                            Title = model.Title,
                            ISBN = model.ISBN
                        };
                        break;

                    case Article.COURSE_NOTES_CODE:
                        newArticle = new CourseNotes()
                        {
                            Course = course,
                            Title = model.Title,
                            SubTitle = "Sample Subtitle",  // FIXME: Inconsistent with Title in Article and there's no Title for Offer.
                            BarCode = model.ISBN
                        };
                        break;

                    case Article.CALCULATOR_CODE:
                        newArticle = new Calculator()
                        {
                            Title = model.Title,
                            Model = model.CalculatorModel,
                            Course = Repository.GetCourseByAcronym("mat145")
                        };
                        break;
                }
                
                Offer offer = new Offer()
                {
                    Price = model.Price,  // FIXME: No elements for this in the view. Weird.
                    Condition = model.Condition,
                    Article = newArticle,
                    ManagedByFair = false,
                    Title = model.Title  // FIXME: No elements for this in the view. Weird.
                };

                if (model.ForNextFair)
                {
                    var nextFair = Repository.GetNextFair();

                    offer.ManagedByFair = true;
                    nextFair?.Offers.Add(offer);
                }

                Repository.AddOffer(offer, toUser: User.Identity.GetUserId());

                if (Session["images"] != null)
                {
                    List<OfferImage> images = Session["images"] as List<OfferImage>;
                    foreach (var image in images)
                    {
                        image.MovePermanently(uploadsPath);
                        offer.AddImage(image);
                    }
                }

                Repository.Update();

                return RedirectToAction("Index", "Home");
            }
            else
            {
                model.Courses = Repository.GetAllCourses().ToList();
                return View(model);
            }
        }

        // GET: Offer/Edit/5
        public ActionResult Edit(string id)
        {
            Offer offer = Repository.GetOfferBy(id);
            Session["images"] = null;
            string[] data = Repository.GetISBNByArticle(offer.Article.Id.ToString());
            var model = new ArticleViewModel()
            {
                Id = id,
                Acronym = offer.Article.Course.Acronym,
                Condition = offer.Condition,
                Title = offer.Title,
                Price = offer.Price,
                Courses = Repository.GetAllCourses().ToList(),
                TypeModel = offer.Article.TypeName,
                Type = offer.Article.ArticleCode,
                ISBN = (data[0] == null)? data[1]: data[0]        
            };

            try
            {
                Calculator calculator = (Calculator) offer.Article;
                model.TypeModel = (calculator.Model == CalculatorModel.VOYAGE200) ?
                    nameof(CalculatorModel.VOYAGE200) : 
                    nameof(CalculatorModel.NSPIRE);
            }
            catch (InvalidCastException ex)
            {
                Console.WriteLine(ex.Message);
            }

            ViewBag.Images = offer.Images;

            return View(model);

        }

        // POST: Offer/Edit/5
        [HttpPost]
        public ActionResult Edit(ArticleViewModel model)
        {
            var course = Repository.GetCourseByAcronym(model.Acronym);

            // Validating the model
             if (model.Type != Article.CALCULATOR_CODE && string.IsNullOrEmpty(model.ISBN))
            {
                ModelState.AddModelError(nameof(ArticleViewModel.ISBN), 
                    "Le type choisi requiert un ISBN ou un code.");
            }

            if (course == null && model.Type != Article.CALCULATOR_CODE)
            {
                ModelState.AddModelError(nameof(ArticleViewModel.Acronym), 
                    "Le type choisi requiert un cours de la liste.");
            }

            // Proceeding to add the new offer.
            if (ModelState.IsValid)
            {
                Offer cOffer = Repository.GetOfferBy(model.Id);
                Article cArticle = cOffer.Article;
                var uploadsPath = Server.MapPath(UPLOADS_PATH);

                Repository.AttachToContext(cOffer);

                //Book
                try
                {
                    Book book = (Book)cArticle;
                    book.Course = course;
                    book.Title = model.Title;
                    book.ISBN = model.ISBN;
                    cOffer.Article = book;
                }
                catch(InvalidCastException ex)
                {
                    Console.WriteLine(ex.Message);
                }

                //Notes de cours
                try
                {
                    CourseNotes courseNote = (CourseNotes)cArticle;
                    courseNote.Course = course;
                    courseNote.Title = model.Title;
                    courseNote.SubTitle = "Sample Subtitle";
                    courseNote.BarCode = model.ISBN;
                    cOffer.Article = courseNote;
                }
                catch (InvalidCastException ex)
                {
                    Console.WriteLine(ex.Message);
                }

                //Calculatrice
                try
                {
                    Calculator calculator = (Calculator)cArticle;
                    calculator.Title = model.Title;
                    calculator.Model = model.CalculatorModel;
                    calculator.Course = course;
                    cOffer.Article = calculator;
                }
                catch (InvalidCastException ex)
                {
                    Console.WriteLine(ex.Message);
                }
                
                cOffer.Price = model.Price;  
                cOffer.Condition = model.Condition;
                cOffer.ManagedByFair = false;
                cOffer.Article = cArticle;
                cOffer.Title = model.Title;  

                if (model.ForNextFair)
                {
                    var nextFair = Repository.GetNextFair();
                    cOffer.ManagedByFair = true;
                    nextFair?.Offers.Add(cOffer);
                }

                Repository.AddOffer(cOffer, toUser: User.Identity.GetUserId());

                if (Session["images"] != null)
                {
                    List<OfferImage> images = Session["images"] as List<OfferImage>;
                    foreach (var image in images)
                    {
                        image.MovePermanently(uploadsPath);
                        cOffer.AddImage(image);
                    }
                }

                Repository.Update();

                return RedirectToAction("Index", "Home");
            }
            else
            {
                model.Courses = Repository.GetAllCourses().ToList();
                return View(model);
            }
        }

        

            // GET: Offer/Delete/5
        public ActionResult Delete(int id)
        {
            return View();
        }

        // POST: Offer/Delete/5
        [HttpPost]
        public ActionResult Delete(int id, FormCollection collection)
        {
            try
            {
                // TODO: Add delete logic here

                return RedirectToAction("Index");
            }
            catch
            {
                return View();
            }
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                _repository?.Dispose();
                _repository = null;
            }

            base.Dispose(disposing);
        }

        #region AJAX

        /// <summary>
        /// Delete offer
        /// </summary>
        /// <param name="offerIds">Offer Id array</param>
        /// <param name="type">Define the choice between Disable and delete offer</param>
        [HttpPost]
        public ActionResult DeleteOffer(string[] offerIds, bool type = false)
        {
            bool status = false;
            string message = null;

            status = (type)?Repository.DeleteOffer(offerIds):
                            Repository.DisableOffer(offerIds);

            if (!status)
                message = "Erreur lors de la suppression";

            return Json(new {
                status = status,
                message = message
            }, contentType: "application/json");
        }

        public JsonResult AddImage(HttpPostedFileBase image)
        {
            var uploadsPath = Server.MapPath(UPLOADS_PATH);
            var offerImage = new OfferImage();
            offerImage.SaveImageTemporarily(image, User.Identity.GetUserId(), uploadsPath);

            if (Session["images"] == null)
            {
                Session["images"] = new List<OfferImage>();
            }

            //offerImage.Id = Guid.NewGuid();
            (Session["images"] as List<OfferImage>).Add(offerImage);
            return Json(new { thumbPath = offerImage.RelativeThumbnailPath }, contentType: "application/json");
        }

        [HttpPost]
        public ActionResult AddNewCourse(string acronym)
        {
            if (Repository.GetCourseByAcronym(acronym) != null)
            {
                return new HttpStatusCodeResult(HttpStatusCode.Conflict);
            }

            // FIXME: Temporary. Eventually, it must be handled correctly
            Repository.AddNewCourse(acronym, title: "Sample Title");  
            var recentlyAddedCourse = Repository.GetCourseByAcronym(acronym);
            return Json(new
            {
                courseId = recentlyAddedCourse.Id,
                acronym = recentlyAddedCourse.Acronym
            }, contentType: "application/json");
        }

        [HttpPost]
        public ActionResult ConcludeSell(string[] offerIds)
        {
            bool status = Repository.ConcludeSell(offerIds);
            string message = null;

            if (!status) message = "Une erreur est survenue.";

            return Json(new
            {
                status = status,
                message = message
            }, contentType: "application/json");
        }

        [HttpPost]
        public ActionResult sendMailForOffer(string to_name, string to_message, string to_address, string to_offer)
        {
            Offer offer = Repository.GetOfferBy(to_offer);
            ApplicationUser userOffer = Repository.GetOfferByUser(offer);

            MailMessage mail = new MailMessage();
            SmtpClient SmtpServer = new SmtpClient(ConfigurationManager.AppSettings["SMTP_CLIENT"]);
            mail.From = new MailAddress(userOffer.Email);
            mail.Subject = "Un utilisateur est intéssé par votre article ";

            mail.IsBodyHtml = true;

            string header_mail = "<div style='background:#629c49;padding:3px 10px;color:black;'>" +
                                    "<div style='float:left;'><h1>TRIBUTERRE</h1></div>" +
                                    "<div style='margin-left:70%;'><h1 style='color:white;'>" +
                                    "Notification de LivrÈTS</h1></div></div></div>";
            string footer_mail = "<br><div style='background:#629c49;padding:3px 10px;color:black;'>" +
                                   "<h1>MERCI</h1></div>";
            string footer_message = "<div><a href='/Offer/Details/" + offer.Id + "'>Article concernée</a></div>";
            string infos_article = "<div>Répondé au mail " + to_address + "</div>" +
                                    "<div><ul>" +
                                    "<li><b>Titre: </b> " + offer.Title + "</li>" +
                                    "<li><b>Cours: </b> " + offer.Article.Course.Acronym + "</li>" +
                                    "<li><b>Poster le: </b> " + offer.StartDate + "</li>" +
                                    "<li><b>Prix: </b> " + offer.Price.ToString("00.00") + "</li>" +
                                    "</ul></div>";

            mail.Body = string.Format("<div>{0}<div><div>{1}</div>{2}{3}</div>{4}" +
                "<div>", header_mail, to_message, infos_article, footer_message, footer_mail);

            SmtpServer.Port = Int32.Parse(ConfigurationManager.AppSettings["EMAIL_PORT"]);
            SmtpServer.Credentials = new NetworkCredential(ConfigurationManager.AppSettings["EMAIL_USERNAME"],
                ConfigurationManager.AppSettings["EMAIL_PWD"]);
            SmtpServer.EnableSsl = true;
            mail.To.Add(to_address);
            SmtpServer.Send(mail);

            return Json(new
            {
                status = true,
                message = "Votre message a été envoyé"
            }, contentType: "application/json");
        }

        //delete image 
        [HttpPost]
        public ActionResult DeleteImg(string[] ImgIds)
        {
            bool status = Repository.DeleteOfferImg(ImgIds);
            string message = null;

            if (!status) message = "Une erreur est survenue.";

            return Json(new
            {
                status = status,
                message = message
            }, contentType: "application/json");
        }

        #endregion
    }
}
