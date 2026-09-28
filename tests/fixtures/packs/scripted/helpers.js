maquettiste.selector('audited', model => model.entities.filter(e => e.hasStereotype('audited')).map(e => e.id));
maquettiste.filter('longName', element => element.name.length > 6);
