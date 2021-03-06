﻿using LiteDB;
using Newtonsoft.Json;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Text.RegularExpressions;

namespace EcoLiteDBExport
{
    public class previous_run_data
    {
        public int id { get; set; }
        public string name { get; set; }
    }
    
    public class data_models
    {
        public int _id { get; set; }
        public int TimeSeconds { get; set; }
        public string Username { get; set; }
        public Guid AuthId { get; set; }
        public Guid WorldObjectId { get; set; }
        public double Value { get; set; }
        public string ItemTypeName { get; set; }
        public string SpeciesName { get; set; }
        public string WorldObjectTypeName { get; set; }
    }

    public class config
    {
        public string api_access_token { get; set; }
        public int timeout { get; set; }
        public int query_limit { get; set; }
        public string api_url { get; set; }
        public string server_dir { get; set; }
        public previous_run_data[] previous_run_data { get; set; }
    }

    class Program
    {
        public const string config_file_name = "config.json";
        public const string previous_run_file_name = "previous_run.json";

        // log current dateTime & memory usage is kilobytes
        static void log(string message)
        {
            Console.WriteLine(
                "[{0}] [{1}] {2}",
                DateTime.Now.ToString("MM/dd/yyyy H:mm:ss"),
                string.Format("{0}{1}", (GC.GetTotalMemory(true) / 1024).ToString(), " KB"),
                message
                );
        }

        // post the data to the api
        static void postData(string api_url, Dictionary<string, List<data_models>> post_data, config config_data)
        {
            log(string.Format("posting data to {0}", api_url));
            using (WebClient wc = new WebClient())
            {
                // set it so the api knows that type of data is being received
                wc.Headers[HttpRequestHeader.ContentType] = "application/json";
                wc.Headers[HttpRequestHeader.Authorization] = config_data.api_access_token;
                // post to the api & transform the api array to JSON
                string HtmlResult = wc.UploadString(
                    api_url,
                    JsonConvert.SerializeObject(post_data)
                );
                log("posted to api");
            }
        }

        // get the config data that includes the api_url & database_file location for example
        static config getConfigData()
        {
            // load config file
            log("loading config data");
            string config_file_location = string.Format("{0}{1}", AppDomain.CurrentDomain.BaseDirectory, config_file_name);
            string config_json = File.ReadAllText(config_file_location);
            return JsonConvert.DeserializeObject<config>(config_json);
        }

        // get the previous runs data so we don't have to get the records we've handled before
        static List<previous_run_data> getPreviousRunData(string previous_run_file_location, config config_data)
        {
            // create previous_run json file if it doesn't exist already
            if (!File.Exists(previous_run_file_location))
            {
                using (StreamWriter w = File.AppendText(previous_run_file_location))
                {
                    w.WriteLine(JsonConvert.SerializeObject(config_data.previous_run_data));
                }
            }

            // read previous run file for the ids 
            string json = File.ReadAllText(previous_run_file_location);
            return JsonConvert.DeserializeObject<List<previous_run_data>>(json);
        }

        static void processNewRecords(config config_data)
        {
            // get the previous run data
            string previous_run_file_location = string.Format("{0}{1}", AppDomain.CurrentDomain.BaseDirectory, previous_run_file_name);
            List<previous_run_data> previous_run_data = getPreviousRunData(previous_run_file_location, config_data);

            DirectoryInfo backup_directory = new DirectoryInfo(config_data.server_dir);
            var storage_directories = Directory.GetDirectories(string.Format("{0}/Storage", config_data.server_dir));
            foreach (string storage in storage_directories)
            {
                if (!storage.Contains("Backup"))
                {
                    continue;
                }

                backup_directory = new DirectoryInfo(string.Format("{0}/Storage/Backup", config_data.server_dir)).GetDirectories()
                    .OrderByDescending(f => f.LastWriteTime)
                    .First();
            }

            // connect to database
            var db = new LiteDatabase(backup_directory.FullName + "/Game.db");

            log("---------------------------");
            // initiate the response array
            Dictionary<string, List<data_models>> api_data = new Dictionary<string, List<data_models>>();
            foreach (var previous_run in previous_run_data)
            {
                log(string.Format("starting {0} collection export", previous_run.name));

                // initiate the current database response
                List<data_models> result = new List<data_models>();

                // connect to the collection
                var collection = db.GetCollection<data_models>(previous_run.name);

                // set index on _id
                collection.EnsureIndex(x => x._id);

                // if its lower than the previous_run.id there has been a reset
                // use this id as the new id
                var latest = collection.FindOne(Query.All(Query.Descending));
                if (latest != null && latest._id < previous_run.id)
                {
                    log("an older database has been placed");
                    previous_run.id = latest._id;
                }

                log(string.Format("executing query for {0} collection", previous_run.name));
                // execute query to get all rows since id
                var query = collection.Find(x => x._id > previous_run.id, limit: config_data.query_limit);
                foreach (var entry in query)
                {
                    // add response to the database response array
                    result.Add(entry);
                    previous_run.id = entry._id;
                }


                // add total result to the main data array that will be sent to the api
                api_data.Add(previous_run.name, result);
                log("---------------------------");
            }
            // close database connection
            db = null;

            // post the data to the api
            postData(config_data.api_url, api_data, config_data);

            log("writing last_id to the previous_id file");
            // write the last id gotten from the query to the file
            // so the next time the script runs it doesn't have to get all the current & previous records
            File.WriteAllText(previous_run_file_location, JsonConvert.SerializeObject(previous_run_data));
        }

        static void Main(string[] args)
        {
            while(true)
            {
                // loop every x minutes
                log("***************************");
                try
                {
                    // get config data
                    config config_data = getConfigData();
                    processNewRecords(config_data);
                    System.Threading.Thread.Sleep(config_data.timeout);
                }
                catch (Exception ex)
                {
                    log("program crashed");
                    // sleep 1 minute if the program crashes
                    System.Threading.Thread.Sleep(1000*60);
                }

            }
        }
    }
}
