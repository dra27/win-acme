﻿using LetsEncrypt.ACME.JOSE;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using Microsoft.Web.Administration;
using System.Threading;
using LetsEncrypt.ACME.PKI;
using System.Security.Cryptography.X509Certificates;
using LetsEncrypt.ACME.HTTP;
using System.Net;
using System.Security.Principal;
using CommandLine.Text;
using CommandLine;
using Microsoft.Win32.TaskScheduler;
using System.Reflection;

namespace LetsEncrypt.ACME.Simple
{
    class Program
    {
        const string clientName = "letsencrypt-win-simple";
        public static string BaseURI { get; set; }

        static string configPath;
        static Settings settings;
        static AcmeClient client;
        public static Options Options;

        static bool IsElevated => new WindowsPrincipal(WindowsIdentity.GetCurrent()).IsInRole(WindowsBuiltInRole.Administrator);

        static void Main(string[] args)
        {
            var commandLineParseResult = Parser.Default.ParseArguments<Options>(args);
            var parsed = commandLineParseResult as Parsed<Options>;
            if (parsed == null)
            {
#if DEBUG
                Console.WriteLine("Press enter to continue.");
                Console.ReadLine();
#endif
                return; // not parsed
            }
            Options = parsed.Value;

            Console.WriteLine("Let's Encrypt (Simple Windows ACME Client)");

            BaseURI = Options.BaseURI;
            if (Options.Test)
                BaseURI = "https://acme-staging.api.letsencrypt.org/";

            //Console.Write("\nUse production Let's Encrypt server? (Y/N) ");
            //if (PromptYesNo())
            //    BaseURI = ProductionBaseURI;

            Console.WriteLine($"\nACME Server: {BaseURI}");

            settings = new Settings(clientName, BaseURI);

            configPath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), clientName, CleanFileName(BaseURI));
            Console.WriteLine("Config Folder: " + configPath);
            Directory.CreateDirectory(configPath);

            try
            {
                using (var signer = new RS256Signer())
                {
                    signer.Init();

                    var signerPath = Path.Combine(configPath, "Signer");
                    if (File.Exists(signerPath))
                    {
                        Console.WriteLine($"Loading Signer from {signerPath}");
                        using (var signerStream = File.OpenRead(signerPath))
                            signer.Load(signerStream);
                    }

                    using (client = new AcmeClient(new Uri(BaseURI), new AcmeServerDirectory(), signer))
                    {
                        client.Init();
                        Console.WriteLine("\nGetting AcmeServerDirectory");
                        client.GetDirectory(true);

                        var registrationPath = Path.Combine(configPath, "Registration");
                        if (File.Exists(registrationPath))
                        {
                            Console.WriteLine($"Loading Registration from {registrationPath}");
                            using (var registrationStream = File.OpenRead(registrationPath))
                                client.Registration = AcmeRegistration.Load(registrationStream);
                        }
                        else
                        {
                            Console.WriteLine("Calling Register");
                            var registration = client.Register(new string[] { });

                            if (!Options.AcceptTOS && !Options.Renew)
                            {
                                Console.WriteLine($"Do you agree to {registration.TosLinkUri}? (Y/N) ");
                                if (!PromptYesNo())
                                    return;
                            }

                            Console.WriteLine("Updating Registration");
                            client.UpdateRegistration(true, true);

                            Console.WriteLine("Saving Registration");
                            using (var registrationStream = File.OpenWrite(registrationPath))
                                client.Registration.Save(registrationStream);

                            Console.WriteLine("Saving Signer");
                            using (var signerStream = File.OpenWrite(signerPath))
                                signer.Save(signerStream);
                        }

                        if (Options.Renew)
                        {
                            CheckRenewals();
#if DEBUG
                            Console.WriteLine("Press enter to continue.");
                            Console.ReadLine();
#endif
                            return;
                        }

                        var targets = new List<Target>();
                        foreach (var plugin in Target.Plugins.Values)
                        {
                            targets.AddRange(plugin.GetTargets());
                        }

                        if (targets.Count == 0)
                        {
                            Console.WriteLine("No targets found.");
                        }
                        else
                        {
                            var count = 1;
                            foreach (var binding in targets)
                            {
                                Console.WriteLine($" {count}: {binding}");
                                count++;
                            }
                        }

                        Console.WriteLine();
                        foreach (var plugin in Target.Plugins.Values)
                        {
                            plugin.PrintMenu();
                        }

                        Console.WriteLine(" A: Get certificates for all hosts");
                        Console.WriteLine(" Q: Quit");
                        Console.Write("Which host do you want to get a certificate for: ");
                        var response = Console.ReadLine().ToLowerInvariant();
                        switch (response)
                        {
                            case "a":
                                foreach (var target in targets)
                                {
                                    Auto(target);
                                }
                                break;
                            case "q":
                                return;
                            default:
                                var targetId = 0;
                                if (Int32.TryParse(response, out targetId))
                                {
                                    targetId--;
                                    if (targetId >= 0 && targetId < targets.Count)
                                    {
                                        var binding = targets[targetId];
                                        Auto(binding);
                                    }
                                }
                                else
                                {
                                    foreach (var plugin in Target.Plugins.Values)
                                    {
                                        plugin.HandleMenuResponse(response, targets);
                                    }
                                }
                                break;
                        }
                    }
                }
            }
            catch (Exception e)
            {
                Console.ForegroundColor = ConsoleColor.Red;
                var acmeWebException = e as AcmeClient.AcmeWebException;
                if (acmeWebException != null)
                {
                    Console.WriteLine(acmeWebException.Message);
                    Console.WriteLine("ACME Server Returned:");
                    Console.WriteLine(acmeWebException.Response.ContentAsString);
                }
                else
                {
                    Console.WriteLine(e);
                }
                Console.ResetColor();
            }

#if DEBUG
            Console.WriteLine("Press enter to continue.");
            Console.ReadLine();
#endif
        }

        static string CleanFileName(string fileName) => Path.GetInvalidFileNameChars().Aggregate(fileName, (current, c) => current.Replace(c.ToString(), string.Empty));

        public static bool PromptYesNo()
        {
            while (true)
            {
                var response = Console.ReadKey(true);
                if (response.Key == ConsoleKey.Y)
                    return true;
                if (response.Key == ConsoleKey.N)
                    return false;
                Console.WriteLine("Please press Y or N.");
            }
        }

        public static void Auto(Target binding)
        {
            var auth = Authorize(binding);
            if (auth.Status == "valid")
            {
                var pfxFilename = GetCertificate(binding);

                if (Options.Test && !Options.Renew)
                {
                    Console.WriteLine($"\nDo you want to install the .pfx into the Certificate Store? (Y/N) ");
                    if (!PromptYesNo())
                        return;
                }

                X509Store store;
                X509Certificate2 certificate;
                InstallCertificate(binding, pfxFilename, out store, out certificate);

                if (Options.Test && !Options.Renew)
                {
                    Console.WriteLine($"\nDo you want to add/update the certificate to your server software? (Y/N) ");
                    if (!PromptYesNo())
                        return;
                }

                binding.Plugin.Install(binding, pfxFilename, store, certificate);

                if (Options.Test && !Options.Renew)
                {
                    Console.WriteLine($"\nDo you want to automatically renew this certificate in 60 days? This will add a task scheduler task. (Y/N) ");
                    if (!PromptYesNo())
                        return;

                }

                if (!Options.Renew)
                    ScheduleRenewal(binding);
            }
        }

        public static void InstallCertificate(Target binding, string pfxFilename, out X509Store store, out X509Certificate2 certificate)
        {
            Console.WriteLine($" Opening Certificate Store");
            store = new X509Store(StoreName.My, StoreLocation.LocalMachine);
            store.Open(OpenFlags.OpenExistingOnly | OpenFlags.ReadWrite);

            Console.WriteLine($" Loading .pfx");

            // See http://paulstovell.com/blog/x509certificate2
            certificate = new X509Certificate2(pfxFilename, "", X509KeyStorageFlags.MachineKeySet | X509KeyStorageFlags.PersistKeySet | X509KeyStorageFlags.Exportable);
            certificate.FriendlyName = $"{binding.Host} {DateTime.Now}";
            
            Console.WriteLine($" Adding Certificate to Store");
            store.Add(certificate);

            Console.WriteLine($" Closing Certificate Store");
            store.Close();
        }

        public static string GetCertificate(Target binding)
        {
            var dnsIdentifier = binding.Host;

            var rsaKeys = CsrHelper.GenerateRsaPrivateKey();
            var csrDetails = new CsrHelper.CsrDetails
            {
                CommonName = dnsIdentifier
            };
            var csr = CsrHelper.GenerateCsr(csrDetails, rsaKeys);
            byte[] derRaw;
            using (var bs = new MemoryStream())
            {
                csr.ExportAsDer(bs);
                derRaw = bs.ToArray();
            }
            var derB64u = JwsHelper.Base64UrlEncode(derRaw);

            Console.WriteLine($"\nRequesting Certificate");
            var certRequ = client.RequestCertificate(derB64u);

            Console.WriteLine($" Request Status: {certRequ.StatusCode}");

            //Console.WriteLine($"Refreshing Cert Request");
            //client.RefreshCertificateRequest(certRequ);

            if (certRequ.StatusCode == System.Net.HttpStatusCode.Created)
            {
                var keyGenFile = Path.Combine(configPath, $"{dnsIdentifier}-gen-key.json");
                var keyPemFile = Path.Combine(configPath, $"{dnsIdentifier}-key.pem");
                var csrGenFile = Path.Combine(configPath, $"{dnsIdentifier}-gen-csr.json");
                var csrPemFile = Path.Combine(configPath, $"{dnsIdentifier}-csr.pem");
                var crtDerFile = Path.Combine(configPath, $"{dnsIdentifier}-crt.der");
                var crtPemFile = Path.Combine(configPath, $"{dnsIdentifier}-crt.pem");
                var crtPfxFile = Path.Combine(configPath, $"{dnsIdentifier}-all.pfx");

                using (var fs = new FileStream(keyGenFile, FileMode.Create))
                {
                    rsaKeys.Save(fs);
                    File.WriteAllText(keyPemFile, rsaKeys.Pem);
                }
                using (var fs = new FileStream(csrGenFile, FileMode.Create))
                {
                    csr.Save(fs);
                    File.WriteAllText(csrPemFile, csr.Pem);
                }

                Console.WriteLine($" Saving Certificate to {crtDerFile}");
                using (var file = File.Create(crtDerFile))
                    certRequ.SaveCertificate(file);

                using (FileStream source = new FileStream(crtDerFile, FileMode.Open), target = new FileStream(crtPemFile, FileMode.Create))
                {
                    CsrHelper.Crt.ConvertDerToPem(source, target);
                }

                // can't create a pfx until we get an irsPemFile, which seems to be some issuer cert thing.
                var isrPemFile = GetIssuerCertificate(certRequ);

                Console.WriteLine($" Saving Certificate to {crtPfxFile} (with no password set)");
                CsrHelper.Crt.ConvertToPfx(keyPemFile, crtPemFile, isrPemFile, crtPfxFile, FileMode.Create);

                return crtPfxFile;
            }

            throw new Exception($"Request status = {certRequ.StatusCode}");
        }

        public static void EnsureTaskScheduler()
        {
            var taskName = $"{clientName} {CleanFileName(BaseURI)}";

            using (var taskService = new TaskService())
            {
                if (settings.ScheduledTaskName == taskName)
                {
                    Console.WriteLine($" Deleting existing Task {taskName} from Windows Task Scheduler.");
                    taskService.RootFolder.DeleteTask(taskName, false);
                }

                Console.WriteLine($" Creating Task {taskName} with Windows Task Scheduler at 9am every day.");

                // Create a new task definition and assign properties
                var task = taskService.NewTask();
                task.RegistrationInfo.Description = "Check for renewal of ACME certificates.";

                var now = DateTime.Now;
                var runtime = new DateTime(now.Year, now.Month, now.Day, 9, 0, 0);
                task.Triggers.Add(new DailyTrigger { DaysInterval = 1, StartBoundary = runtime });

                var currentExec = Assembly.GetExecutingAssembly().Location;

                // Create an action that will launch Notepad whenever the trigger fires
                task.Actions.Add(new ExecAction(currentExec, $"--renew --baseuri \"{BaseURI}\"", Path.GetDirectoryName(currentExec)));

                task.Principal.RunLevel = TaskRunLevel.Highest; // need admin
                task.Settings.Hidden = true; // don't pop up a command prompt every day

                // Register the task in the root folder
                taskService.RootFolder.RegisterTaskDefinition(taskName, task);

                settings.ScheduledTaskName = taskName;
            }
        }

        const float renewalPeriod = 60; // can't easily make this a command line option since it would have to be saved

        public static void ScheduleRenewal(Target target)
        {
            EnsureTaskScheduler();

            var renewals = settings.LoadRenewals();

            foreach (var existing in from r in renewals.ToArray() where r.Binding.Host == target.Host select r)
            {
                Console.WriteLine($" Removing existing scheduled renewal {existing}");
                renewals.Remove(existing);
            }

            var result = new ScheduledRenewal() { Binding = target, Date = DateTime.UtcNow.AddDays(renewalPeriod) };
            renewals.Add(result);
            settings.SaveRenewals(renewals);

            Console.WriteLine($" Renewal Scheduled {result}");
        }

        public static void CheckRenewals()
        {
            Console.WriteLine("Checking Renewals");

            var renewals = settings.LoadRenewals();
            if (renewals.Count == 0)
                Console.WriteLine(" No scheduled renewals found.");

            var now = DateTime.UtcNow;
            foreach (var renewal in renewals)
            {
                Console.WriteLine($" Checking {renewal}");
                if (renewal.Date < now)
                {
                    Console.WriteLine($" Renewing certificate for {renewal}");
                    Auto(renewal.Binding);

                    renewal.Date = DateTime.UtcNow.AddDays(renewalPeriod);
                    settings.SaveRenewals(renewals);
                    Console.WriteLine($" Renewal Scheduled {renewal}");
                }
            }
        }

        public static string GetIssuerCertificate(CertificateRequest certificate)
        {
            var linksEnum = certificate.Links;
            if (linksEnum != null)
            {
                var links = new LinkCollection(linksEnum);
                var upLink = links.GetFirstOrDefault("up");
                if (upLink != null)
                {
                    var tmp = Path.GetTempFileName();
                    try
                    {
                        using (var web = new WebClient())
                        {
                            //if (v.Proxy != null)
                            //    web.Proxy = v.Proxy.GetWebProxy();

                            var uri = new Uri(new Uri(BaseURI), upLink.Uri);
                            web.DownloadFile(uri, tmp);
                        }

                        var cacert = new X509Certificate2(tmp);
                        var sernum = cacert.GetSerialNumberString();
                        var tprint = cacert.Thumbprint;
                        var sigalg = cacert.SignatureAlgorithm?.FriendlyName;
                        var sigval = cacert.GetCertHashString();

                        var cacertDerFile = Path.Combine(configPath, $"ca-{sernum}-crt.der");
                        var cacertPemFile = Path.Combine(configPath, $"ca-{sernum}-crt.pem");

                        if (!File.Exists(cacertDerFile))
                            File.Copy(tmp, cacertDerFile, true);

                        Console.WriteLine($" Saving Issuer Certificate to {cacertPemFile}");
                        if (!File.Exists(cacertPemFile))
                            CsrHelper.Crt.ConvertDerToPem(cacertDerFile, cacertPemFile);

                        return cacertPemFile;
                    }
                    finally
                    {
                        if (File.Exists(tmp))
                            File.Delete(tmp);
                    }
                }
            }

            return null;
        }

        public static AuthorizationState Authorize(Target target)
        {
            var dnsIdentifier = target.Host;
            var webRootPath = target.WebRootPath;

            Console.WriteLine($"\nAuthorizing Identifier {dnsIdentifier} Using Challenge Type {AcmeProtocol.CHALLENGE_TYPE_HTTP}");
            var authzState = client.AuthorizeIdentifier(dnsIdentifier);
            var challenge = client.GenerateAuthorizeChallengeAnswer(authzState, AcmeProtocol.CHALLENGE_TYPE_HTTP);
            var answerPath = Environment.ExpandEnvironmentVariables(Path.Combine(webRootPath, challenge.ChallengeAnswer.Key));

            Console.WriteLine($" Writing challenge answer to {answerPath}");
            var directory = Path.GetDirectoryName(answerPath);
            Directory.CreateDirectory(directory);
            File.WriteAllText(answerPath, challenge.ChallengeAnswer.Value);

            target.Plugin.BeforeAuthorize(target, answerPath);

            var answerUri = new Uri(new Uri("http://" + dnsIdentifier), challenge.ChallengeAnswer.Key);
            Console.WriteLine($" Answer should now be browsable at {answerUri}");

            try
            {
                Console.WriteLine(" Submitting answer");
                authzState.Challenges = new AuthorizeChallenge[] { challenge };
                client.SubmitAuthorizeChallengeAnswer(authzState, AcmeProtocol.CHALLENGE_TYPE_HTTP, true);

                // have to loop to wait for server to stop being pending.
                // TODO: put timeout/retry limit in this loop
                while (authzState.Status == "pending")
                {
                    Console.WriteLine(" Refreshing authorization");
                    Thread.Sleep(1000); // this has to be here to give ACME server a chance to think
                    var newAuthzState = client.RefreshIdentifierAuthorization(authzState);
                    if (newAuthzState.Status != "pending")
                        authzState = newAuthzState;
                }

                Console.WriteLine($" Authorization Result: {authzState.Status}");
                if (authzState.Status == "invalid")
                {
                    Console.ForegroundColor = ConsoleColor.Red;
                    Console.WriteLine("\n******************************************************************************");
                    Console.WriteLine($"The ACME server was probably unable to reach {answerUri}");

                    Console.WriteLine("\nCheck in a browser to see if the answer file is being served correctly.");

                    target.Plugin.OnAuthorizeFail(target);

                    Console.WriteLine("\n******************************************************************************");
                    Console.ResetColor();
                }

                //if (authzState.Status == "valid")
                //{
                //    var authPath = Path.Combine(configPath, dnsIdentifier + ".auth");
                //    Console.WriteLine($" Saving authorization record to: {authPath}");
                //    using (var authStream = File.Create(authPath))
                //        authzState.Save(authStream);
                //}

                return authzState;
            }
            finally
            {
                if (authzState.Status == "valid")
                {
                    Console.WriteLine(" Deleting answer");
                    File.Delete(answerPath);
                }
            }
        }
    }
}