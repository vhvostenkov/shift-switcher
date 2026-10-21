"""Server-owned product catalogue. Clients cannot choose price or fulfillment."""
import re

DEFAULT_PRODUCT = 'shift-switcher-pro-v1'
DEFAULT_CATALOG = {
    DEFAULT_PRODUCT: dict(name='Shift Switcher Pro 1.x', amount=49900, currency='RUB',
        fulfillment='device_license', devices=2, terms='/shift-switcher/terms.html',
        privacy='/shift-switcher/privacy.html', download='/shift-switcher/',
        terms_version='2026-10-20-draft', privacy_version='2026-10-20-draft')
}


def catalog_from(config):
    products = config.get('products', DEFAULT_CATALOG)
    if not isinstance(products, dict) or not products: raise ValueError('empty_catalog')
    for key, product in products.items():
        if not re.fullmatch(r'[a-z0-9][a-z0-9-]{2,80}', key): raise ValueError('invalid_product')
        if type(product.get('amount')) is not int or not 0 < product['amount'] <= 10000000: raise ValueError('invalid_price')
        if product.get('currency') != 'RUB' or product.get('fulfillment') != 'device_license': raise ValueError('unsupported_fulfillment')
        if type(product.get('devices')) is not int or not 1 <= product['devices'] <= 20: raise ValueError('invalid_device_limit')
        if not isinstance(product.get('name'), str) or not 1 <= len(product['name']) <= 120: raise ValueError('invalid_name')
        for field in ['terms','privacy','download']:
            value=product.get(field,'')
            if not value.startswith('/') or value.startswith('//') or '\\' in value: raise ValueError('local_product_url_required')
    return products
