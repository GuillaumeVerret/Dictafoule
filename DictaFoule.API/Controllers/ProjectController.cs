﻿using DictaFoule.Common.Enum;
using DictaFoule.Common.Tools;
using DictaFoule.Common.DAL;
using System;
using System.Web.Http;
using System.Data.Entity;
using DictaFoule.API.Models.Project;
using System.Linq;
using System.Net.Http;
using System.Net;
using System.IO;
using SendGrid;
using SendGrid.Helpers.Mail;
using System.Drawing;
using System.Configuration;
using Stripe;
using Newtonsoft.Json;
using Xceed.Words.NET;

namespace DictaFoule.API.Controllers
{
    public class ProjectController : BaseController
    {
        [HttpPost]
        [Route("v1/Project/Create")]
        public IHttpActionResult Create(Object model)
        {
            var jsonmodel = model.ToString();
            var ImportFile = JsonConvert.DeserializeObject<SoundFileModel>(jsonmodel);
            var user = entities.users.Where(a => a.guid == ImportFile.Guid).ToList();
            if (user.Count == 0)
                return BadRequest("User not found");
            try
            {
                var file = DecodeEncodeTo64.DecodeFrom64(ImportFile.File64);
                file.Position = 0;
                if (file == null)
                    return BadRequest("Le fichier est null");
                if (file.Length == 0)
                    return BadRequest("Le fichier est vide");
                if (!DataValidation.IsMp3(ImportFile.Name) && !DataValidation.IsWav(ImportFile.Name))
                    return BadRequest("Le fichier n'est pas au bon format");
                var importFile = new project
                {
                    creation_date = DateTime.UtcNow,
                    state = 0,
                    import_sound_file_name = ImportFile.Name,
                    id_user = user.FirstOrDefault().id
                };
                entities.projects.Add(importFile);
                entities.SaveChanges();


                var absoluteUri = AzureBlobStorage.Upload(file, "audio/mpeg", ImportFile.Name, "import");

                if (string.IsNullOrWhiteSpace(absoluteUri))
                    return BadRequest("Un problème est survenu pendant l'upload du fichier");

                importFile.import_sound_file_uri = absoluteUri;
                entities.Entry(importFile).State = EntityState.Modified;
                entities.SaveChanges();

                LogTools.Add_log(LogLevel.INFO, " API CREATE PROJECT", importFile.id, "new project" );
                return Ok(importFile.id);
            }
            catch (Exception ex)
            {
                LogTools.Add_log(LogLevel.DANGER, " API CREATE PROJECT", 0, "error create project " + ex.Message);
                new ApplicationException("Erreur application", ex);
                return BadRequest(ex.Message);
            }
        }

        [HttpGet]
        [Route("v1/Project/GetTranscrib")]
        public HttpResponseMessage GetTransbrib(int id_project, string guidElements)
        {
            var project = entities.projects.Find(id_project);
            if (project == null)
                return new HttpResponseMessage(HttpStatusCode.NotFound);
            var id_user = entities.users.Find(project.id_user);
            if (id_user.guid != guidElements)
                return new HttpResponseMessage(HttpStatusCode.NotFound);
            var response = new HttpResponseMessage(HttpStatusCode.OK);
            var soundlines = entities.sound_line.Where(sl => sl.id_project == id_project).ToList();
            if (soundlines.Count == 0)
                return new HttpResponseMessage(HttpStatusCode.NotFound);
            string result = String.Empty;
            foreach (var txt in soundlines)
            {
                result += txt.task_answer;
            }
            response.Content = new StringContent(result, System.Text.Encoding.UTF8, "text/plain");
            return response;
        }
        [HttpGet]
        [Route("v1/Project/GetStateProject")]
        public IHttpActionResult GetStateProject(int id_project, string guidElements)
        {
            var project = entities.projects.Find(id_project);
            if (project == null)
                return BadRequest("User or Project not Found");
            var id_user = entities.users.Find(project.id_user);
            if (id_user == null && id_user.guid != guidElements)
                return BadRequest("User or Project not Found");
            return Ok(project.state);
        }

        
        [HttpGet]
        [Route("v1/Project/GetIdProject")]
        public IHttpActionResult GetIdProject(string nameFile, string guidElements)
        {
            var user = entities.users.Where(a => a.guid == guidElements).ToList();
            if (user.Count == 0)
                return NotFound();
            var id_user = user.FirstOrDefault().id;
            var project = entities.projects.Where(p => p.import_sound_file_name == nameFile && p.id_user == id_user).ToList();
            if (project.Count == 0)
                return NotFound();
            else
                return Ok(project.FirstOrDefault().id);
        }

        
        [HttpPost]
        [Route("v1/Project/SendEmail")]
        public async System.Threading.Tasks.Task<IHttpActionResult> SendEmail(Object model)
        {
            var jsonmodel = model.ToString();
            var emailModel = JsonConvert.DeserializeObject<SendEmailModel>(jsonmodel);
            var user = entities.users.Where(u => u.guid == emailModel.GuidElements).ToList();
            if (user.Count == 0)
                return NotFound();
            var texts = entities.sound_line.Where(p => p.id_project == emailModel.IdProject).ToList();
            if (texts.Count == 0)
                return NotFound();
            var project = entities.projects.Find(emailModel.IdProject);
            MemoryStream stream = new MemoryStream();
            DocX doc = DocX.Create(stream);

            foreach (var text in texts)
            {
                Paragraph par = doc.InsertParagraph();
                par.Append(text.task_answer).Font("Times New Roman");
                doc.Save();
            }

            var client = new SendGridClient(ConfigurationManager.AppSettings["APISENDGREED"]);
            var msg = new SendGridMessage()
            {
                From = new EmailAddress("transcription@dictafoule.com"),
                Subject = "Dictafoule Transcription " + project.import_sound_file_name,
                PlainTextContent = "Bonjour,\n Votre transcription se trouve en piece jointe.\n Cordialement,\n l'équipe de Dictafoule.",
            };
            stream.Seek(0, SeekOrigin.Begin);
            var nameproject = project.import_sound_file_name.Split('.');
            msg.AddAttachment(nameproject.FirstOrDefault() + ".docx", Convert.ToBase64String(stream.ToArray()));
            msg.AddTo(new EmailAddress(emailModel.Email));
            try
            {
                var response = await client.SendEmailAsync(msg);
                user.FirstOrDefault().email = emailModel.Email;
                entities.Entry(user.FirstOrDefault()).State = EntityState.Modified;
                LogTools.Add_log(LogLevel.INFO, "API SEND EMAIL", emailModel.IdProject, "Succes send email to " + emailModel.Email);
                return Ok();
            }
            catch (Exception ex)
            {
                LogTools.Add_log(LogLevel.DANGER, "API SEND EMAIL", emailModel.IdProject, "Fail send email to " + emailModel.Email + " ,error :"+ ex.Message);
                return InternalServerError(ex);
            }
        }

        [HttpPost]
        [Route("v1/Project/Payment")]
        public IHttpActionResult Payement(PaymentModel paymentModel)
        {
            string idproject = Convert.ToString(paymentModel.IdProject);
            var project = entities.projects.Find(paymentModel.IdProject);

            var charge = new ChargeCreateOptions
            {
                Amount = Convert.ToInt32(paymentModel.Amount * 100),
                Currency = "eur",
                Description = String.Format("Dictafoule project {0} - projectName {1} ", idproject, project.import_sound_file_name),
                SourceId = paymentModel.Token
            };

            var service = new ChargeService(ConfigurationManager.AppSettings["APISTRIPE"]);

            try
            {
                var response = service.Create(charge);
                if (response.Status == "succeeded")
                {
                    var payment = new order
                    {
                        id_stripe = response.Id,
                        amount = unchecked((int)response.Amount),
                        id_project = paymentModel.IdProject,
                        email = paymentModel.Email,
                        name_customer = paymentModel.Name,
                        date = DateTime.UtcNow
                    };
                    entities.orders.Add(payment);
                    entities.SaveChanges();
                    ProjectTools.UpdateProjectState(paymentModel.IdProject, ProjectState.SoundCut);
                   AzureQueueStorage.QueueProject(paymentModel.IdProject, "soundcut");
                }

            }
            catch (StripeException ex)
            {
                StripeError stripeError = ex.StripeError;
                LogTools.Add_log(LogLevel.DANGER, "API STRIPE PAYMENT", paymentModel.IdProject, ex.Message);
                return InternalServerError(ex);

            }
            LogTools.Add_log(LogLevel.INFO, "API STRIPE PAYMENT", paymentModel.IdProject, "Payment succeeded");
            return Ok(true);
        }

        //[HttpPost]
        //public IHttpActionResult SendInvoice()
        //{
        //    StripeConfiguration.SetApiKey(ConfigurationManager.AppSettings["APISTRIPE"]);

        //    var invoiceOptions = new StripeInvoiceCreateOptions()
        //    {
        //        Description = "",
        //        TaxPercent = 20
        //    };

        //    var invoiceService = new StripeInvoiceService();
        //    StripeInvoice invoice = invoiceService.Create("cus_DCUTDRmBduxBrG", invoiceOptions);
        //}
    }
}