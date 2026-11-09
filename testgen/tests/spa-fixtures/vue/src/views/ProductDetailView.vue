<script setup lang="ts">
import { ref } from 'vue';
import { useRoute } from 'vue-router';
import axios from 'axios';
import { addToCart } from '../api/products';

const route = useRoute();
const product = ref<{ name: string } | null>(null);
axios.get(`/api/products/${route.params.id}`).then((r) => (product.value = r.data));
</script>

<template>
  <article>
    <h1>{{ product?.name }}</h1>
    <button type="button" @click="addToCart(route.params.id)">Lägg i varukorgen</button>
    <RouterLink to="/products">Tillbaka</RouterLink>
  </article>
</template>
