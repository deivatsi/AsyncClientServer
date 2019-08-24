using System;
using System.Collections.Generic;
using System.Numerics;
using System.Text;

namespace NetCore.Console.Client.MessageContracts
{
	[Serializable]
	public class TestObject
	{


		public string LastName { get; set; }
		public string FirstName { get; set; }
		public int ZipCode { get; set; }

		public TestObject(string lName, string fName, int zipCode)
		{
			LastName = lName;
			FirstName = fName;
			ZipCode = zipCode;
		}

		public TestObject()
		{

		}
	}
}
