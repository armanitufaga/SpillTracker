﻿using HtmlAgilityPack;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;
using SpillTracker.Models;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;

namespace SpillTracker.Controllers
{
    [Authorize(Roles = "admin")]
    public class AdminController : Controller
    {
        private readonly SpillTrackerDbContext dbSpllTracker;

        public AdminController(SpillTrackerDbContext context)
        {
            dbSpllTracker = context;
        }

        public IActionResult Index()
        {
            //ScrapeEPCRAtable();
            return View();
        }


        public IActionResult ScrapeEPCRAtable(string xpath)
        {
            // Load the EPCRA site and get the table of Extremely Hazardous Substances
            string url = "https://www.ecfr.gov/cgi-bin/text-idx?SID=5bda0c1c4736b83aaf402bed85944e07&mc=true&node=pt40.30.355&rgn=div5#ap40.30.355_161.a";
            string defaultXpath = "/html/body/div/div[2]/div[2]/div[43]/div[2]/table";
            HtmlWeb web = new HtmlWeb();

            //Debug.WriteLine("\n\n xpath: " + xpath + "\n\n");

            try
            {
                // Load the webpage and use LINQ to parse HTML table into a list of tabel rows 
                HtmlDocument doc = web.Load(url);
                IEnumerable<HtmlNode> nodes = doc.DocumentNode.SelectNodes(xpath);
                IEnumerable<HtmlNode> htmlTableRowsList = from table in nodes
                                                          from row in table.SelectNodes("tr").Cast<HtmlNode>()
                                                          select row;

                bool skipTableHeader = true;
                foreach (HtmlNode row in htmlTableRowsList)
                {
                    if (skipTableHeader == true)
                    {
                        skipTableHeader = false;
                        continue;
                    }

                    // Use LINQ to parse HTML TR into list of cells
                    IEnumerable<HtmlNode> trCells = from cell in row.SelectNodes("th|td").Cast<HtmlNode>() select cell;

                    // Pull out relevant data from the table row
                    string parsedCas = String.Concat(trCells.ElementAt(0).InnerHtml.Where(c => !Char.IsWhiteSpace(c)));
                    string parsedName = trCells.ElementAt(1).InnerHtml;
                    double parsedRQ = Convert.ToDouble(trCells.ElementAt(3).InnerHtml);
                    //double parsedRQ, value;
                    //if (double.TryParse(trCells.ElementAt(3).InnerHtml, out value))
                    //{
                    //    parsedRQ = value;
                    //}

                    Chemical parsedChem = new Chemical
                    {
                        CasNum = parsedCas,
                        Name = parsedName,
                        ReportableQuantity = parsedRQ,
                        ReportableQuantityUnits = "lbs",
                        EpcraChem = true
                    };

                    ProcessChemical(parsedChem);
                }

                UpdateStatusTime("EPCRA Scraper");
            }
            catch (Exception ex)
            {
                Debug.WriteLine(ex);
            }
            return Json(true);
        }

        public IActionResult ScrapeCERCLAtable(string xpath)
        {
            // Load the CERCLA site and get the table of Hazardous Substances
            string url = "https://www.ecfr.gov/cgi-bin/text-idx?SID=cf9016ebebd8898fcd57f71e1b66a7af&mc=true&node=se40.30.302_14&rgn=div8";
            HtmlWeb web = new HtmlWeb();
            string defaultXpath = "/html/body/div/div[2]/div[2]/div[4]/div[2]/table";
            try
            {
                // Load the webpage and use LINQ to parse HTML table into a list of tabel rows 
                HtmlDocument doc = web.Load(url);
                IEnumerable<HtmlNode> nodes = doc.DocumentNode.SelectNodes(xpath);
                IEnumerable<HtmlNode> htmlTableRowsList = from table in nodes
                                                          from row in table.SelectNodes("tr").Cast<HtmlNode>()
                                                          select row;
                bool skipTableHeader = true;
                foreach (HtmlNode row in htmlTableRowsList)
                {
                    if (skipTableHeader == true)
                    {
                        skipTableHeader = false;
                        continue;
                    }

                    // Use LINQ to parse HTML TR into list of cells
                    IEnumerable<HtmlNode> trCells = from cell in row.SelectNodes("th|td").Cast<HtmlNode>() select cell;

                    // Filter the RQ column to start parsing around the table
                    string[] rqStrArr = trCells.ElementAt(4).InnerHtml.Split('('); // Filters to [0] lbs value and [1] kg value
                    
                    if (String.IsNullOrEmpty(rqStrArr[0])) // When the RQ is blank, its assumed the chemical is a sublisting of some parent and thus use the RQ of the parent
                    {
                        // Pull out relevant data from the table row 
                        string parsedName = trCells.ElementAt(0).InnerHtml;
                        string[] casNumArr = trCells.ElementAt(1).InnerHtml.Split("<br>"); // For chems with many CAS nums listed. splits them in a list to be processed individually 
                        
                        Chemical lastChem = dbSpllTracker.Chemicals.AsEnumerable().Last();

                        foreach (string casNum in casNumArr)
                        {
                            Chemical parsedChem = new Chemical
                            {
                                CasNum = String.Concat(casNum.Where(c => !Char.IsWhiteSpace(c))),
                                Name = parsedName,
                                ReportableQuantity = lastChem.ReportableQuantity,
                                ReportableQuantityUnits = "lbs",
                                CerclaChem = true
                            };

                            ProcessChemical(parsedChem);
                        }
                    }
                    else if (rqStrArr[0].Contains("**")) // ** means no rq for the broad term so skip this listing on the table
                    {
                        Debug.WriteLine("\n\n skipping " + trCells.ElementAt(0).InnerHtml + " becasue RQ is **");
                        continue;
                    }
                    else if (trCells.ElementAt(3).InnerHtml.Contains('D')) // Should filter out section of table showing all Unlisted Hazardous Wastes Characteristics of Corrosivity, Ignitability, Reactivity, and Toxicity which would all be duplicates on the table
                    {
                        Debug.WriteLine("\n\n skipping " + trCells.ElementAt(0).InnerHtml);
                        continue;
                    }
                    else if (trCells.ElementAt(0).InnerHtml.Contains("Radionuclides (including radon)")) // This row refers to Appendix B listing all Radionuclides. This individual row should be skipped
                    {
                        Debug.WriteLine("\n\n skipping " + trCells.ElementAt(0).InnerHtml);
                        continue;
                    }
                    else
                    {
                        // Pull out relevant data from the table row 
                        double parsedRQ = Convert.ToDouble(rqStrArr[0]);
                        string parsedName = trCells.ElementAt(0).InnerHtml;
                        string[] casNumArr = trCells.ElementAt(1).InnerHtml.Split("<br>"); // For chems with many CAS nums listed. splits them in a list to be processed individually 

                        foreach (string casNum in casNumArr)
                        {
                            Chemical parsedChem = new Chemical
                            {
                                CasNum = String.Concat(casNum.Where(c => !Char.IsWhiteSpace(c))),
                                Name = parsedName,
                                ReportableQuantity = parsedRQ,
                                ReportableQuantityUnits = "lbs",
                                CerclaChem = true
                            };

                            ProcessChemical(parsedChem);
                        }
                    }

                    if (trCells.ElementAt(0).InnerHtml.Equals("Zirconium tetrachloride"))
                    {
                        break; // last chemical we care about. break out of loop and stop capturing data on the table
                    }
                }

                UpdateStatusTime("CERCLA Scraper");
            }
            catch (Exception ex)
            {
                Debug.WriteLine(ex);
            }

            return Json(true);
        }

        public void ProcessChemical(Chemical parsedChem)
        {
            if (dbSpllTracker.Chemicals.Any(c => c.CasNum == parsedChem.CasNum && c.Name == parsedChem.Name && c.ReportableQuantity == parsedChem.ReportableQuantity))
            {
                Debug.WriteLine("{{{ NAME:" + parsedChem.Name + ", CAS:" + parsedChem.CasNum + ", RQ:" + parsedChem.ReportableQuantity + "}}} exists in the database, skipping entry...");
            }
            else if (dbSpllTracker.Chemicals.Any(c => c.CasNum == parsedChem.CasNum && c.Name == parsedChem.Name && c.ReportableQuantity != parsedChem.ReportableQuantity))
            {
                Debug.WriteLine("{{{ NAME:" + parsedChem.Name + ", CAS:" + parsedChem.CasNum + "}}} exists in the database but reportable quantity of " + parsedChem.ReportableQuantity
                    + " lbs doesn't match the existing reportable quantity of " + dbSpllTracker.Chemicals.Where(c => c.CasNum == parsedChem.CasNum).FirstOrDefault().ReportableQuantity
                    + " lbs. Updating RQ in database...");
                dbSpllTracker.Chemicals.Where(c => c.CasNum == parsedChem.CasNum).FirstOrDefault().ReportableQuantity = parsedChem.ReportableQuantity;
            }
            else if (dbSpllTracker.Chemicals.Any(c => c.CasNum == parsedChem.CasNum && c.ReportableQuantity == parsedChem.ReportableQuantity && c.Name != parsedChem.Name))
            {
                Debug.WriteLine("{{{ CAS:" + parsedChem.CasNum + ", RQ:" + parsedChem.ReportableQuantity + "}}} exists in the database but its name, " + parsedChem.Name + ", did not match the existing name,"
                    + dbSpllTracker.Chemicals.Where(c => c.CasNum == parsedChem.CasNum).FirstOrDefault().Name + ". Adding " + parsedChem.Name
                    + " as a synonym to chemical " + parsedChem.CasNum);

                if (String.IsNullOrEmpty(dbSpllTracker.Chemicals.Where(c => c.CasNum == parsedChem.CasNum).FirstOrDefault().Aliases))
                {
                    dbSpllTracker.Chemicals.Where(c => c.CasNum == parsedChem.CasNum).FirstOrDefault().Aliases += parsedChem.Name + "<br>";
                }
                else
                {
                    string[] aliasArr = dbSpllTracker.Chemicals.Where(c => c.CasNum == parsedChem.CasNum).FirstOrDefault().Aliases.Split("<br>");

                    if (aliasArr.Contains(parsedChem.Name) == false)
                    {
                        if (parsedChem.EpcraChem == true && dbSpllTracker.Chemicals.Where(c => c.CasNum == parsedChem.CasNum).FirstOrDefault().EpcraChem == false)
                        {
                            dbSpllTracker.Chemicals.Where(c => c.CasNum == parsedChem.CasNum).FirstOrDefault().EpcraChem = true;
                        }

                        if (parsedChem.CerclaChem == true && dbSpllTracker.Chemicals.Where(c => c.CasNum == parsedChem.CasNum).FirstOrDefault().CerclaChem == false)
                        {
                            dbSpllTracker.Chemicals.Where(c => c.CasNum == parsedChem.CasNum).FirstOrDefault().CerclaChem = true;
                        }

                        dbSpllTracker.Chemicals.Where(c => c.CasNum == parsedChem.CasNum).FirstOrDefault().Aliases += parsedChem.Name + "<br>";
                    }
                }
            }
            else
            {
                Debug.WriteLine("{{{ NAME:" + parsedChem.Name + ", CAS:" + parsedChem.CasNum + ", RQ:" + parsedChem.ReportableQuantity + "}}} doesn't exist in the database. Adding the substance to the list...");
                dbSpllTracker.Add(parsedChem);
            }
            dbSpllTracker.SaveChanges();
        }

        public void UpdateStatusTime(string source)
        {
            StatusTime newStatusTime = new StatusTime();
            newStatusTime.SourceName = source;
            newStatusTime.Time = DateTime.UtcNow;

            dbSpllTracker.StatusTimes.Add(newStatusTime);
            dbSpllTracker.SaveChanges();
        }
    }
}