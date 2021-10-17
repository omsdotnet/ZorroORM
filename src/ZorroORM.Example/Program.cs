using System;
using System.Data.SQLite;

namespace ZorroORM.Example
{
  class Program
  {
    static void Main(string[] args)
    {
      string cs = "Data Source=test.db;Version=3";

      using var connection = new SQLiteConnection(cs);

      connection.Open();

      var sc = connection.rpc<IDataStorage>();
      
      Console.WriteLine(sc.GetCount());
      
      Console.ReadLine();
    }
  }
}
