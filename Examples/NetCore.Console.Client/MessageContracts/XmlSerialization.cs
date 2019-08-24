using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.Serialization.Formatters.Binary;
using System.Text;
using System.Xml;
using System.Xml.Serialization;
using SimpleSockets.Messaging.MessageContract;
using SimpleSockets.Messaging.MessageContracts;

namespace NetCore.Console.Client.MessageContracts
{
	public class XmlSerialization: IObjectSerializer
	{

		public byte[] SerializeObjectToBytes(object anySerializableObject)
		{
			try
			{
				XmlSerializer xmlSer = new XmlSerializer(anySerializableObject.GetType());
				string xml;
				using (var sww = new StringWriter())
				{
					using (XmlWriter writer = XmlWriter.Create(sww))
					{
						xmlSer.Serialize(writer, anySerializableObject);
						xml =sww.ToString();
					}
				}

				return Encoding.UTF8.GetBytes(xml);
			}
			catch (Exception ex)
			{
				throw new Exception("Unable to serialize the object of type " + anySerializableObject.GetType() + " to an xml string.",ex);
			}
		}

		public object DeserializeBytesToObject(byte[] bytes, Type type)
		{
			try
			{
				var xml = Encoding.UTF8.GetString(bytes);
				XmlSerializer xmlSer = new XmlSerializer(type);
				StringReader reader = new StringReader(xml);
				return xmlSer.Deserialize(reader);
			}
			catch (Exception ex)
			{
				throw new Exception("Unable to convert xml string back to an object. \n" + ex.ToString());
			}
		}

	}
}
