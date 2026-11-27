using Maquettiste.Testing;

namespace Maquettiste.Packs.Tests;

/// <summary>
/// A small shop whose entities are all bound to designed tables of one database (no entity mapping, nothing projected), with seeds, for
/// sql-ddl's entity seeds through bindings (retire-projection-design.md section 6):
/// <list type="bullet">
/// <item>Customer in <c>customers (id, name, city, geo_lat, geo_lng)</c>: its address's city and its map point, two levels down, in columns;
/// its contacts (a list of a value object) in <c>customer_contacts (customer_id, position, name, phone)</c> with an order column; its labels
/// (a list of a string) in <c>customer_labels (customer_id, label)</c>.</item>
/// <item>Product in <c>products (id, name)</c>.</item>
/// <item>Payment in <c>payments (id, kind, amount, reference, customer_id, payer_note)</c> with the type column <c>kind</c>; BankTransfer in
/// the same table (kind <c>bank</c>); CardPayment in <c>card_payments (payment_id, last4)</c>, joined by its base foreign key (kind
/// <c>card</c>).</item>
/// <item>Pays (one customer, many payments) through the foreign key <c>payments.customer_id</c>, its attribute payerNote in
/// <c>payments.payer_note</c>; Favorites (customers and products, many to many) through <c>customer_products (customer_id, product_id,
/// rank)</c>, its attribute rank in <c>rank</c>.</item>
/// </list>
/// Seeds: two customers (Ada with two contacts and two labels, Grace with none and one), two products, a payment, a bank transfer and a card
/// payment, two links of Pays (the payment to Ada, the card payment to Grace with a note), and three favorites.
/// </summary>
internal static class BoundSeedModel
{
    public static string Id(int n) => "01KBSEED" + n.ToString("D18", System.Globalization.CultureInfo.InvariantCulture);

    /// <summary>The model with sql-ddl writing under db/, its one database <c>main</c> of the given dialect.</summary>
    public static PackRepo Repo(string dialect)
    {
        var repo = PackRepo.Blank();
        PackRepo.CopyTree(Path.Combine(Fixtures.RepoRoot, "packs", "sql-ddl"), Path.Combine(repo.Repo.ModelRoot, "templates", "sql-ddl"));
        repo.Write(".maquettiste/maquettiste.json", """
            {
              "formatVersion": 1,
              "name": "bound-seeds",
              "outputs": { "allow": [ { "path": "db" } ] },
              "packs": { "sql-ddl": { "output": "db" } }
            }

            """);
        var m = ".maquettiste/model/";
        var t = m + "databases/main/tables/";
        repo.Write(m + "packages/shop.json", $$"""{"kind":"package","id":"{{Id(1)}}","name":"Shop"}""");
        repo.Write(m + "databases/main/database.json", $$"""{"kind":"database","id":"{{Id(2)}}","name":"main","dialect":"{{dialect}}","byConvention":"none"}""");

        repo.Write(m + "types/geo-point.json", $$"""
            {"kind":"value-object","id":"{{Id(3)}}","name":"GeoPoint","package":"{{Id(1)}}","attributes":[
              {"id":"{{Id(4)}}","name":"latitude","type":"decimal","precision":9,"scale":6,"required":true},
              {"id":"{{Id(5)}}","name":"longitude","type":"decimal","precision":9,"scale":6,"required":true}]}
            """);
        repo.Write(m + "types/postal-address.json", $$"""
            {"kind":"value-object","id":"{{Id(6)}}","name":"PostalAddress","package":"{{Id(1)}}","attributes":[
              {"id":"{{Id(7)}}","name":"city","type":"string","length":80,"required":true},
              {"id":"{{Id(8)}}","name":"geo","type":{"ref":"{{Id(3)}}"} }]}
            """);
        repo.Write(m + "types/contact.json", $$"""
            {"kind":"value-object","id":"{{Id(9)}}","name":"Contact","package":"{{Id(1)}}","attributes":[
              {"id":"{{Id(10)}}","name":"name","type":"string","length":60,"required":true},
              {"id":"{{Id(11)}}","name":"phone","type":"string","length":20}]}
            """);

        repo.Write(t + "customers.json", $$"""
            {"kind":"table","id":"{{Id(20)}}","name":"customers","database":"{{Id(2)}}","columns":[
              {"id":"{{Id(21)}}","name":"id","type":"int64","nullable":false},
              {"id":"{{Id(22)}}","name":"name","type":"string","length":60,"nullable":false},
              {"id":"{{Id(23)}}","name":"city","type":"string","length":80},
              {"id":"{{Id(24)}}","name":"geo_lat","type":"decimal","precision":9,"scale":6},
              {"id":"{{Id(25)}}","name":"geo_lng","type":"decimal","precision":9,"scale":6}],
             "primaryKey":{"columns":["{{Id(21)}}"]} }
            """);
        repo.Write(t + "customer-contacts.json", $$"""
            {"kind":"table","id":"{{Id(30)}}","name":"customer_contacts","database":"{{Id(2)}}","columns":[
              {"id":"{{Id(31)}}","name":"customer_id","type":"int64","nullable":false},
              {"id":"{{Id(32)}}","name":"position","type":"int32","nullable":false},
              {"id":"{{Id(33)}}","name":"name","type":"string","length":60,"nullable":false},
              {"id":"{{Id(34)}}","name":"phone","type":"string","length":20}],
             "primaryKey":{"columns":["{{Id(31)}}","{{Id(32)}}"]},
             "foreignKeys":[{"id":"{{Id(35)}}","name":"fk_customer_contacts_customer","columns":["{{Id(31)}}"],"referencesTable":"{{Id(20)}}","onDelete":"cascade"}]}
            """);
        repo.Write(t + "customer-labels.json", $$"""
            {"kind":"table","id":"{{Id(40)}}","name":"customer_labels","database":"{{Id(2)}}","columns":[
              {"id":"{{Id(41)}}","name":"customer_id","type":"int64","nullable":false},
              {"id":"{{Id(42)}}","name":"label","type":"string","length":40,"nullable":false}],
             "primaryKey":{"columns":["{{Id(41)}}","{{Id(42)}}"]},
             "foreignKeys":[{"id":"{{Id(43)}}","name":"fk_customer_labels_customer","columns":["{{Id(41)}}"],"referencesTable":"{{Id(20)}}","onDelete":"cascade"}]}
            """);
        repo.Write(t + "products.json", $$"""
            {"kind":"table","id":"{{Id(50)}}","name":"products","database":"{{Id(2)}}","columns":[
              {"id":"{{Id(51)}}","name":"id","type":"int64","nullable":false},
              {"id":"{{Id(52)}}","name":"name","type":"string","length":60,"nullable":false}],
             "primaryKey":{"columns":["{{Id(51)}}"]} }
            """);
        repo.Write(t + "customer-products.json", $$"""
            {"kind":"table","id":"{{Id(55)}}","name":"customer_products","database":"{{Id(2)}}","columns":[
              {"id":"{{Id(56)}}","name":"customer_id","type":"int64","nullable":false},
              {"id":"{{Id(57)}}","name":"product_id","type":"int64","nullable":false},
              {"id":"{{Id(58)}}","name":"rank","type":"int32"}],
             "primaryKey":{"columns":["{{Id(56)}}","{{Id(57)}}"]},
             "foreignKeys":[{"id":"{{Id(59)}}","name":"fk_customer_products_customer","columns":["{{Id(56)}}"],"referencesTable":"{{Id(20)}}","onDelete":"cascade"},
               {"id":"{{Id(60)}}","name":"fk_customer_products_product","columns":["{{Id(57)}}"],"referencesTable":"{{Id(50)}}","onDelete":"cascade"}]}
            """);
        repo.Write(t + "payments.json", $$"""
            {"kind":"table","id":"{{Id(65)}}","name":"payments","database":"{{Id(2)}}","columns":[
              {"id":"{{Id(66)}}","name":"id","type":"int64","nullable":false},
              {"id":"{{Id(67)}}","name":"kind","type":"string","length":10,"nullable":false},
              {"id":"{{Id(68)}}","name":"amount","type":"decimal","precision":12,"scale":2,"nullable":false},
              {"id":"{{Id(69)}}","name":"reference","type":"string","length":64},
              {"id":"{{Id(70)}}","name":"customer_id","type":"int64"},
              {"id":"{{Id(71)}}","name":"payer_note","type":"string","length":100}],
             "primaryKey":{"columns":["{{Id(66)}}"]},
             "foreignKeys":[{"id":"{{Id(72)}}","name":"fk_payments_customer","columns":["{{Id(70)}}"],"referencesTable":"{{Id(20)}}"}]}
            """);
        repo.Write(t + "card-payments.json", $$"""
            {"kind":"table","id":"{{Id(75)}}","name":"card_payments","database":"{{Id(2)}}","columns":[
              {"id":"{{Id(76)}}","name":"payment_id","type":"int64","nullable":false},
              {"id":"{{Id(77)}}","name":"last4","type":"string","length":4,"nullable":false}],
             "primaryKey":{"columns":["{{Id(76)}}"]},
             "foreignKeys":[{"id":"{{Id(78)}}","name":"fk_card_payments_payment","columns":["{{Id(76)}}"],"referencesTable":"{{Id(65)}}","onDelete":"cascade"}]}
            """);

        repo.Write(m + "entities/customer.json", $$"""
            {"kind":"entity","id":"{{Id(100)}}","name":"Customer","package":"{{Id(1)}}","key":{"attributes":["{{Id(101)}}"]},"attributes":[
              {"id":"{{Id(101)}}","name":"id","type":"int64","required":true},
              {"id":"{{Id(102)}}","name":"name","type":"string","length":60,"required":true},
              {"id":"{{Id(103)}}","name":"address","type":{"ref":"{{Id(6)}}"} },
              {"id":"{{Id(104)}}","name":"contacts","type":{"ref":"{{Id(9)}}"},"collection":true},
              {"id":"{{Id(105)}}","name":"labels","type":"string","length":40,"collection":true}],
             "bindings":[{"id":"{{Id(106)}}","database":"{{Id(2)}}","source":"{{Id(20)}}","fields":[
                {"attribute":"{{Id(101)}}","column":"{{Id(21)}}"},
                {"attribute":"{{Id(102)}}","column":"{{Id(22)}}"},
                {"attribute":"{{Id(103)}}.{{Id(7)}}","column":"{{Id(23)}}"},
                {"attribute":"{{Id(103)}}.{{Id(8)}}.{{Id(4)}}","column":"{{Id(24)}}"},
                {"attribute":"{{Id(103)}}.{{Id(8)}}.{{Id(5)}}","column":"{{Id(25)}}"}],
               "childTables":[
                {"id":"{{Id(107)}}","attribute":"{{Id(104)}}","table":"{{Id(30)}}","ownerForeignKey":"{{Id(35)}}","orderColumn":"{{Id(32)}}","fields":[
                  {"attribute":"{{Id(104)}}.{{Id(10)}}","column":"{{Id(33)}}"},
                  {"attribute":"{{Id(104)}}.{{Id(11)}}","column":"{{Id(34)}}"}]},
                {"id":"{{Id(108)}}","attribute":"{{Id(105)}}","table":"{{Id(40)}}","ownerForeignKey":"{{Id(43)}}","fields":[
                  {"attribute":"{{Id(105)}}","column":"{{Id(42)}}"}]}]}]}
            """);
        repo.Write(m + "entities/product.json", $$"""
            {"kind":"entity","id":"{{Id(110)}}","name":"Product","package":"{{Id(1)}}","key":{"attributes":["{{Id(111)}}"]},"attributes":[
              {"id":"{{Id(111)}}","name":"id","type":"int64","required":true},
              {"id":"{{Id(112)}}","name":"name","type":"string","length":60,"required":true}],
             "bindings":[{"id":"{{Id(113)}}","database":"{{Id(2)}}","source":"{{Id(50)}}","fields":[
                {"attribute":"{{Id(111)}}","column":"{{Id(51)}}"},{"attribute":"{{Id(112)}}","column":"{{Id(52)}}"}]}]}
            """);
        repo.Write(m + "entities/payment.json", $$"""
            {"kind":"entity","id":"{{Id(120)}}","name":"Payment","package":"{{Id(1)}}","key":{"attributes":["{{Id(121)}}"]},"attributes":[
              {"id":"{{Id(121)}}","name":"id","type":"int64","required":true},
              {"id":"{{Id(122)}}","name":"amount","type":"decimal","precision":12,"scale":2,"required":true}],
             "bindings":[{"id":"{{Id(123)}}","database":"{{Id(2)}}","source":"{{Id(65)}}","fields":[
                {"attribute":"{{Id(121)}}","column":"{{Id(66)}}"},{"attribute":"{{Id(122)}}","column":"{{Id(68)}}"},{"attribute":"{{Id(141)}}","column":"{{Id(70)}}"}],
               "columns":[{"column":"{{Id(69)}}","status":"ignored"}],
               "discriminatorColumn":"{{Id(67)}}","discriminatorValue":"payment"}]}
            """);
        repo.Write(m + "entities/bank-transfer.json", $$"""
            {"kind":"entity","id":"{{Id(125)}}","name":"BankTransfer","package":"{{Id(1)}}","base":"{{Id(120)}}","attributes":[
              {"id":"{{Id(126)}}","name":"reference","type":"string","length":64,"required":true}],
             "bindings":[{"id":"{{Id(127)}}","database":"{{Id(2)}}","source":"{{Id(65)}}","fields":[
                {"attribute":"{{Id(121)}}","column":"{{Id(66)}}"},{"attribute":"{{Id(122)}}","column":"{{Id(68)}}"},{"attribute":"{{Id(126)}}","column":"{{Id(69)}}"},
                {"attribute":"{{Id(141)}}","column":"{{Id(70)}}"}],
               "discriminatorValue":"bank"}]}
            """);
        repo.Write(m + "entities/card-payment.json", $$"""
            {"kind":"entity","id":"{{Id(130)}}","name":"CardPayment","package":"{{Id(1)}}","base":"{{Id(120)}}","attributes":[
              {"id":"{{Id(131)}}","name":"last4","type":"string","length":4,"required":true}],
             "bindings":[{"id":"{{Id(132)}}","database":"{{Id(2)}}","source":"{{Id(75)}}","baseForeignKey":"{{Id(78)}}","fields":[
                {"attribute":"{{Id(131)}}","column":"{{Id(77)}}"}],
               "discriminatorValue":"card"}]}
            """);

        repo.Write(m + "relations/pays.json", $$"""
            {"kind":"relation","id":"{{Id(140)}}","name":"Pays","package":"{{Id(1)}}","ends":[
              {"id":"{{Id(141)}}","entity":"{{Id(100)}}","role":"customer","max":1},
              {"id":"{{Id(142)}}","entity":"{{Id(120)}}","role":"payments","navigation":"customer","max":"*"}],
             "attributes":[{"id":"{{Id(143)}}","name":"payerNote","type":"string","length":100}]}
            """);
        repo.Write(m + "mappings/pays-in-main.json", $$"""
            {"kind":"mapping","id":"{{Id(144)}}","name":"pays in main","database":"{{Id(2)}}","relation":"{{Id(140)}}","foreignKey":"{{Id(72)}}",
             "attributes":[{"attribute":"{{Id(143)}}","column":"{{Id(71)}}"}]}
            """);
        repo.Write(m + "relations/favorites.json", $$"""
            {"kind":"relation","id":"{{Id(150)}}","name":"Favorites","package":"{{Id(1)}}","ends":[
              {"id":"{{Id(151)}}","entity":"{{Id(100)}}","role":"fans","max":"*"},
              {"id":"{{Id(152)}}","entity":"{{Id(110)}}","role":"favorites","max":"*"}],
             "attributes":[{"id":"{{Id(153)}}","name":"rank","type":"int32"}]}
            """);
        repo.Write(m + "mappings/favorites-in-main.json", $$"""
            {"kind":"mapping","id":"{{Id(154)}}","name":"favorites in main","database":"{{Id(2)}}","relation":"{{Id(150)}}","junctionTable":"{{Id(55)}}",
             "ends":[{"end":"{{Id(151)}}","foreignKey":"{{Id(59)}}"},{"end":"{{Id(152)}}","foreignKey":"{{Id(60)}}"}],
             "attributes":[{"attribute":"{{Id(153)}}","column":"{{Id(58)}}"}]}
            """);

        repo.Write(m + "seeds/customer/customers.json", $$"""
            {"kind":"seed","id":"{{Id(200)}}","name":"customers","target":"{{Id(100)}}","columns":["{{Id(101)}}","{{Id(102)}}","{{Id(103)}}","{{Id(104)}}","{{Id(105)}}"],
             "rows":[
              {"id":"{{Id(201)}}","values":[1,"Ada",{"city":"London","geo":{"latitude":51.5,"longitude":-0.12} },[{"name":"Bob","phone":"555-0100"},{"name":"Cy"}],["vip","early"]]},
              {"id":"{{Id(202)}}","values":[2,"Grace",{"city":"Arlington"},[],["navy"]]}]}
            """);
        repo.Write(m + "seeds/product/products.json", $$"""
            {"kind":"seed","id":"{{Id(210)}}","name":"products","target":"{{Id(110)}}","columns":["{{Id(111)}}","{{Id(112)}}"],
             "rows":[{"id":"{{Id(211)}}","values":[10,"Widget"]},{"id":"{{Id(212)}}","values":[11,"Gadget"]}]}
            """);
        repo.Write(m + "seeds/payment/payments.json", $$"""
            {"kind":"seed","id":"{{Id(220)}}","name":"payments","target":"{{Id(120)}}","columns":["{{Id(121)}}","{{Id(122)}}"],
             "rows":[{"id":"{{Id(221)}}","values":[100,12.5]}]}
            """);
        repo.Write(m + "seeds/bank-transfer/bank-transfers.json", $$"""
            {"kind":"seed","id":"{{Id(225)}}","name":"bankTransfers","target":"{{Id(125)}}","columns":["{{Id(121)}}","{{Id(122)}}","{{Id(126)}}"],
             "rows":[{"id":"{{Id(226)}}","values":[101,40,"REF-1"]}]}
            """);
        repo.Write(m + "seeds/card-payment/card-payments.json", $$"""
            {"kind":"seed","id":"{{Id(230)}}","name":"cardPayments","target":"{{Id(130)}}","columns":["{{Id(121)}}","{{Id(122)}}","{{Id(131)}}"],
             "rows":[{"id":"{{Id(231)}}","values":[102,99.99,"4242"]}]}
            """);
        repo.Write(m + "seeds/pays/pays.json", $$"""
            {"kind":"seed","id":"{{Id(240)}}","name":"pays","target":"{{Id(140)}}","columns":["{{Id(141)}}","{{Id(142)}}","{{Id(143)}}"],
             "rows":[{"id":"{{Id(241)}}","values":["{{Id(202)}}","{{Id(231)}}","card on file"]},{"id":"{{Id(242)}}","values":["{{Id(201)}}","{{Id(221)}}",null]}]}
            """);
        repo.Write(m + "seeds/favorites/favorites.json", $$"""
            {"kind":"seed","id":"{{Id(245)}}","name":"favorites","target":"{{Id(150)}}","columns":["{{Id(151)}}","{{Id(152)}}","{{Id(153)}}"],
             "rows":[{"id":"{{Id(246)}}","values":["{{Id(201)}}","{{Id(211)}}",1]},{"id":"{{Id(247)}}","values":["{{Id(201)}}","{{Id(212)}}",2]},
               {"id":"{{Id(248)}}","values":["{{Id(202)}}","{{Id(211)}}",1]}]}
            """);
        return repo;
    }
}
